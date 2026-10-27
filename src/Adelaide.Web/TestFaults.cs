using Adelaide.Core;

namespace Adelaide.Web;

/// <summary>
/// FAILURE-COMPOSITION harness extension (investigation/failure-composition). Strictly an ADDITION to
/// <see cref="TestMode"/>: every member here is unreachable unless ADELAIDE_TEST_MODE=1 AND the specific variable
/// below is set. It is kept in its own file so the HTTP-surface audit's staged TestMode.cs stays byte-identical.
///
/// WHY IT EXISTS. TestMode's <c>NullFiler</c> throws on every filer call, so the harness can model only ONE half of a
/// partial failure — "the external effect failed". The half that produces the dangerous states is the OTHER one:
/// the external effect SUCCEEDED and the ledger append that records it did not. That is the shape behind both bugs
/// this investigation started from (reprocess deleting the SharePoint item before recording it; split taking no
/// mutual exclusion), and it cannot be reached without (a) a filer that succeeds and (b) a way to fail one append.
///
/// NO OUTBOUND CALL IS INTRODUCED. <see cref="SucceedingFiler"/> is a pure local stub: it returns a synthetic
/// locator and never opens a socket. TestMode's BlockedHandler and NoNetworkCredential remain in force underneath —
/// this only changes what the FILER SEAM returns, not whether the process can reach the network.
/// </summary>
internal static class TestFaults
{
    internal const string FilerVar = "ADELAIDE_TEST_FILER";
    internal const string AppendFailVar = "ADELAIDE_TEST_APPEND_FAIL";

    /// <summary>Synthetic locator returned by <see cref="SucceedingFiler"/>. Recognisable on sight in a ledger dump,
    /// and impossible to confuse with a real driveItem id.</summary>
    internal const string SyntheticDriveItemId = "SYNTHETIC-FILED-DRIVEITEM";

    /// <summary>True when the filer seam should SUCCEED locally instead of throwing.</summary>
    private static bool FilerSucceeds =>
        string.Equals(Environment.GetEnvironmentVariable(FilerVar), "succeed", StringComparison.Ordinal);

    /// <summary>Event types whose append must fail, comma-separated. Empty set when unset.</summary>
    private static HashSet<string> FailingAppendTypes =>
        new((Environment.GetEnvironmentVariable(AppendFailVar) ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

    /// <summary>Thrown by the injected append fault. A distinct type so a test asserts on the INJECTED failure and
    /// never mistakes an unrelated store error for it.</summary>
    internal sealed class InjectedAppendFailure(string type)
        : Exception($"ADELAIDE_TEST_APPEND_FAIL: append of '{type}' deliberately failed (failure-composition harness).");

    /// <summary>
    /// Apply the fault seams. MUST be called after <see cref="TestMode.Apply"/> so the filer rebinding wins over
    /// TestMode's NullFiler, and after the IAuditStore registration so the decorator wraps the real store.
    /// </summary>
    internal static void Apply(WebApplicationBuilder builder)
    {
        if (FilerSucceeds)
        {
            builder.Services.AddTransient<Adelaide.SharePoint.IFiler>(_ => new SucceedingFiler());
            builder.Services.AddTransient<IDocumentSink>(_ => new SucceedingFiler());
        }

        var failTypes = FailingAppendTypes;
        if (failTypes.Count > 0)
        {
            // Decorate the EXISTING IAuditStore registration. Registration is last-wins, and IAuditLog is registered
            // as a delegation to IAuditStore, so decorating the store covers both seams with one wrapper.
            var existing = builder.Services.LastOrDefault(d => d.ServiceType == typeof(IAuditStore))
                ?? throw new InvalidOperationException("TestFaults: no IAuditStore registration to decorate.");
            var innerFactory = existing.ImplementationFactory
                ?? throw new InvalidOperationException("TestFaults: IAuditStore is not registered as a factory.");

            builder.Services.AddSingleton<IAuditStore>(sp =>
                new FaultingAuditStore((IAuditStore)innerFactory(sp), failTypes));
        }
    }

    /// <summary>A filer that SUCCEEDS locally. Models "the external effect landed" without any network.</summary>
    private sealed class SucceedingFiler : Adelaide.SharePoint.IFiler, IDocumentSink
    {
        public Task<Adelaide.SharePoint.FilingResult> FileAsync(Adelaide.SharePoint.FilingSpec spec, CancellationToken ct = default) =>
            Task.FromResult(new Adelaide.SharePoint.FilingResult(SyntheticDriveItemId, PdfADeclared: false, DiRendition: false));

        public Task FileAsync(IncomingDocument doc, DocumentUnit unit, ProcessingResult result, CancellationToken ct = default) =>
            Task.CompletedTask;

        /// <summary>Existence is CONFIRMED — the synthetic item "exists". Only reachable in filer=succeed mode, where
        /// the premise under test is precisely that the external effect landed.</summary>
        public Task<bool> ExistsByIdAsync(string driveItemId, Adelaide.SharePoint.FilingTarget? target = null, CancellationToken ct = default) => Task.FromResult(true);

        /// <summary>Delete succeeds. Pairs with an injected append failure to model "the item is gone and the ledger
        /// never recorded it" — the reclassify/supersede half of the partial-failure matrix.</summary>
        public Task<bool> TryDeleteByIdAsync(string driveItemId, Adelaide.SharePoint.FilingTarget? target = null, CancellationToken ct = default) => Task.FromResult(true);
    }

    /// <summary>
    /// An <see cref="IAuditStore"/> that fails the append of nominated event types and passes everything else
    /// through untouched. Deliberately a pass-through for every QUERY: a test must see the ledger exactly as the
    /// app left it, including the state the failed append did not write.
    /// </summary>
    private sealed class FaultingAuditStore(IAuditStore inner, HashSet<string> failTypes) : IAuditStore
    {
        public Task<AuditEvent> AppendAsync(AuditEntry entry, CancellationToken ct = default) =>
            failTypes.Contains(entry.Type)
                ? throw new InjectedAppendFailure(entry.Type)
                : inner.AppendAsync(entry, ct);

        public Task<DocumentAudit?> GetDocumentAsync(string documentId, CancellationToken ct = default) => inner.GetDocumentAsync(documentId, ct);
        public Task<IReadOnlyList<DocumentAudit>> ListDocumentsAsync(int limit = 5000, CancellationToken ct = default) => inner.ListDocumentsAsync(limit, ct);
        public Task<IReadOnlyList<AuditEvent>> GetEventsAsync(string documentId, CancellationToken ct = default) => inner.GetEventsAsync(documentId, ct);
        public Task<ChainVerification> VerifyChainAsync(CancellationToken ct = default) => inner.VerifyChainAsync(ct);
        public Task<IReadOnlyList<DocumentAudit>> ListActiveAsync(int limit = 300, CancellationToken ct = default) => inner.ListActiveAsync(limit, ct);
        public Task<IReadOnlyList<DocumentAudit>> ListReconcilableAsync(int limit = 100_000, CancellationToken ct = default) => inner.ListReconcilableAsync(limit, ct);
        public Task<IReadOnlyList<DocumentAudit>> ListByStateAsync(string state, int limit = 5000, CancellationToken ct = default) => inner.ListByStateAsync(state, limit, ct);
        public Task<IReadOnlyList<DocumentAudit>> ListDuplicatesAsync(int limit = 5000, CancellationToken ct = default) => inner.ListDuplicatesAsync(limit, ct);
        public Task<int> CountDuplicatesAsync(CancellationToken ct = default) => inner.CountDuplicatesAsync(ct);
        public Task<IReadOnlyDictionary<string, int>> CountsByStateAsync(CancellationToken ct = default) => inner.CountsByStateAsync(ct);
        public Task<StalledDocuments> CountStalledAsync(DateTimeOffset activeOlderThan, DateTimeOffset queuedOlderThan, CancellationToken ct = default) => inner.CountStalledAsync(activeOlderThan, queuedOlderThan, ct);
        public Task<DocumentAudit?> FindBySha256Async(string sha256, string excludeDocumentId, CancellationToken ct = default) => inner.FindBySha256Async(sha256, excludeDocumentId, ct);
        public Task<DocumentAudit?> FindFiledBySha256Async(string sha256, string excludeDocumentId, CancellationToken ct = default) => inner.FindFiledBySha256Async(sha256, excludeDocumentId, ct);
        public Task<DocumentAudit?> FindBySourceRecordIdAsync(string sourceRecordId, string excludeDocumentId, CancellationToken ct = default) => inner.FindBySourceRecordIdAsync(sourceRecordId, excludeDocumentId, ct);
        public Task<DocumentAudit?> FindFiledByFileNamePrefixAsync(string prefix, string excludeDocumentId, CancellationToken ct = default) => inner.FindFiledByFileNamePrefixAsync(prefix, excludeDocumentId, ct);
        public Task<string?> FindDocumentIdByDetailAsync(string needle, CancellationToken ct = default) => inner.FindDocumentIdByDetailAsync(needle, ct);
        public Task<string?> FindDocumentIdByNameAsync(string needle, CancellationToken ct = default) => inner.FindDocumentIdByNameAsync(needle, ct);
        public Task<int> CountDocumentsWithEventSinceAsync(string type, DateTimeOffset since, CancellationToken ct = default) => inner.CountDocumentsWithEventSinceAsync(type, since, ct);
        public Task<int> CountReviewedThenFiledSinceAsync(DateTimeOffset since, CancellationToken ct = default) => inner.CountReviewedThenFiledSinceAsync(since, ct);
    }
}

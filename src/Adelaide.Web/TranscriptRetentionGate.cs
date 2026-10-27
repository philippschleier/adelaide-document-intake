using Adelaide.Core;

// CAPTURE IS UNCONDITIONAL; THIS GATE CONFIGURES ONLY THE DELETION WINDOW.
//
// History, because the semantics flipped (docs/CAPTURE.md has the full story): ADELAIDE_TRANSCRIPT_RETENTION_DAYS
// used to be the master switch for whether anything was WRITTEN at all — unset meant the store was never even
// constructed, which is why the corpus sat at 4 rows while the pipeline processed real documents. Writes no longer
// consult it: every processed document's transcript, artifacts, corpus row and model-call records are captured
// always. What the variable now means:
//
//   ADELAIDE_TRANSCRIPT_RETENTION_DAYS  purge window in days. Unset/0/invalid => NO age-based purge, keep forever
//                                       (erasure via DeleteAsync/ForgetAsync is independent of this).
//   Set to a positive number            CONTENT rows older than the window (never the corpus/corpus_fields
//                                       measurement tables — see TranscriptStore.PurgeOlderThanAsync) are deleted,
//                                       driven from the UploadProcessor at startup and each reconcile tick — and ONLY
//                                       while PurgeGate.Enabled (the evidence-preservation kill-switch) also allows
//                                       deletion. Both must agree before anything is removed.
//
// ADELAIDE_TRANSCRIPT_SAMPLE_RATE is retired and ignored: it selected which non-gated documents were captured, and
// selection at write time is the failure mode this build removes. ADELAIDE_TRANSCRIPT_DB still overrides the store
// location. All values are read ONCE at startup so the decision cannot flip mid-run; the startup banner states the
// resolved mode.
/// <summary>Thrown at startup when the resolved capture substrate cannot survive a restart — the C1 property,
/// restored narrowly. Named so the failure is greppable in a deploy log and cannot be mistaken for an incidental
/// configuration exception. See <see cref="TranscriptRetentionGate.DurabilityError"/>.</summary>
internal sealed class CaptureDurabilityException(string message) : InvalidOperationException(message);

internal static class TranscriptRetentionGate
{
    internal static readonly TranscriptRetentionPolicy Policy =
        PolicyFrom(Environment.GetEnvironmentVariable("ADELAIDE_TRANSCRIPT_RETENTION_DAYS"));

    /// <summary>The env-var → policy mapping, factored out so it is testable with arbitrary inputs. Anything
    /// unparseable or out of range fails to the NO-WINDOW end — a typo'd window must not silently become an
    /// enormous one, and with capture unconditional the safe default for DELETION is "don't".</summary>
    internal static TranscriptRetentionPolicy PolicyFrom(string? retentionDays) => new(
        RetentionDays: int.TryParse(retentionDays, System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 0);

    /// <summary>True when a purge WINDOW is configured (nothing about whether capture writes — it always does).</summary>
    internal static bool Enabled => Policy.Enabled;

    /// <summary>C3 substrate selection — what the C1 guard evolved into, factored pure so it stays tested: cloud
    /// mode (Azure SQL audit substrate) with no explicit override ⇒ the Azure SQL capture store, so the dangerous
    /// omission C1 policed no longer exists as a default; anything else ⇒ SQLite at the resolved path (the escape
    /// hatch, WARNed at startup in cloud mode — durability is operator-owned there). Blankness is
    /// IsNullOrWhiteSpace throughout: a whitespace-only override is unset, never a literal path.</summary>
    internal static (bool AzureSql, string? SqlitePath) SelectSubstrate(bool cloudMode, string? transcriptDbEnv, string auditDbPath) =>
        cloudMode && string.IsNullOrWhiteSpace(transcriptDbEnv)
            ? (true, null)
            : (false, string.IsNullOrWhiteSpace(transcriptDbEnv)
                ? Adelaide.Audit.TranscriptStore.DefaultPathFor(auditDbPath)
                : transcriptDbEnv);

    /// <summary>True when this process is running in a CONTAINER — read that breadth literally, it is deliberate.
    /// Two signals, either sufficient: the Dockerfile's own ADELAIDE_CONTAINER marker, and DOTNET_RUNNING_IN_CONTAINER,
    /// which every `mcr.microsoft.com/dotnet/*` base image sets. The second is not merely a back-compat fallback for
    /// images built before the marker existed — it is the dominant trigger, and it means ANY containerised run of
    /// this app (a local `docker run`, a future containerised job) demands a durable capture substrate. That is the
    /// intended trade: container disk is ephemeral wherever the container runs, and the alternative is a guard that
    /// can be bypassed by not setting a variable. Pure, so the guard below is testable without an environment.</summary>
    internal static bool InContainer(string? adelaideContainer, string? dotnetRunningInContainer) =>
        Truthy(adelaideContainer) || Truthy(dotnetRunningInContainer);

    private static bool Truthy(string? v) =>
        v is "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>C1 RESTORED, narrowly — one property, not the deleted guard's several. In the container image the
    /// local filesystem is ephemeral, so a capture store that resolves to a FILE PATH there loses every transcript,
    /// model call and adjudication on the next restart — silently, because capture writes are fail-open-but-counted
    /// and nothing else enforces the precondition. Returns the operator-facing message (resolved substrate +
    /// resolved path + the variable to fix), or null when the configuration is durable.
    ///
    /// WHAT IT CATCHES, stated exactly — the loose version of this ("a typo in ADELAIDE_SQL_SERVER") is wrong and
    /// was corrected by adversarial review: <see cref="Adelaide.Audit.AzureSqlAuditStore.ConnectionStringFromEnv"/>
    /// tests only IsNullOrWhiteSpace, so a MISTYPED SERVER VALUE still yields a valid connection string and cloud
    /// mode still resolves — that failure surfaces later as SQL errors, not here. This guard fires on the settings
    /// being ABSENT: unset, blank, or supplied under a misspelled SETTING NAME (the App Service app-settings typo).
    /// Those are the cases that silently hand capture to SQLite on container disk.
    ///
    /// DELIBERATE CONSEQUENCE: the ADELAIDE_TRANSCRIPT_DB escape hatch is a LOCALHOST affordance only — in a
    /// container it is a startup failure, not a warning (docs/CAPTURE.md §"Substrate durability"). Note the guard
    /// keys on substrate KIND, not on whether the path happens to be durable: a SQLite file on a mounted Azure
    /// Files share would survive a restart and is still refused, because "is this mount durable?" is not knowable
    /// from a path and a guard that guesses is worse than one that is blunt. Local (non-container) runs are
    /// untouched: SQLite beside the audit DB is the correct store there.</summary>
    internal static string? DurabilityError(bool inContainer, bool azureSql, string? resolvedPath, string? transcriptDbEnv)
    {
        if (!inContainer || azureSql) return null;
        var fix = string.IsNullOrWhiteSpace(transcriptDbEnv)
            ? "ADELAIDE_SQL_SERVER + ADELAIDE_SQL_DATABASE (or ADELAIDE_SQL_CONNECTION_STRING) — one of them is unset, blank, or spelled wrong AS A SETTING NAME, so cloud mode did not resolve and capture fell back to the SQLite default. A wrong server VALUE would not land here; it would fail later as a SQL error"
            : "ADELAIDE_TRANSCRIPT_DB — the SQLite escape hatch is set; unset it so capture resolves to the Azure SQL twin. It is a localhost affordance and has no durable form in a container, even on a mounted share";
        return "Capture substrate is not durable. Running in a container, but the capture store resolved to "
             + $"substrate=SQLite path={resolvedPath}. Container disk does not survive a restart, and capture writes "
             + $"fail open — the loss would be silent. Fix {fix}.";
    }

    /// <summary>Whether the SQLite escape hatch is in play — SET, on any host, cloud or not. Deliberately NOT
    /// conditioned on a resolved SQL connection: the case that most needs announcing is an explicit override while
    /// the intended cloud substrate did NOT resolve, and the old `sqlConn != null` guard made exactly that case
    /// silent. In the container the same configuration is fatal (<see cref="DurabilityError"/>); everywhere else it
    /// is a WARN.</summary>
    internal static bool EscapeHatchActive(string? transcriptDbEnv) => !string.IsNullOrWhiteSpace(transcriptDbEnv);

    /// <summary>The SQLite path in local mode — beside the audit DB unless overridden, so it inherits the same
    /// tenant/residency boundary as the PHI already on that volume.</summary>
    internal static string StorePath(string auditDbPath) =>
        SelectSubstrate(cloudMode: false, Environment.GetEnvironmentVariable("ADELAIDE_TRANSCRIPT_DB"), auditDbPath).SqlitePath!;

    /// <summary>One line for the startup banner: the resolved capture/purge mode.</summary>
    internal static string Describe() => !Policy.Enabled
        ? "capture=always purgeWindow=off (ADELAIDE_TRANSCRIPT_RETENTION_DAYS unset)"
        : $"capture=always purgeWindow={Policy.RetentionDays}d (runs only while ADELAIDE_PURGE_ENABLED; currently {(PurgeGate.Enabled ? "ARMED" : "held by the purge kill-switch")})";
}

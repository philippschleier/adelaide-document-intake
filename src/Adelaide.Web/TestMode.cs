using Azure.Core;
using Microsoft.Extensions.Http;

namespace Adelaide.Web;

/// <summary>
/// HTTP-surface test harness isolation (ADELAIDE_TEST_MODE=1). NOT a production code path: every member here is
/// unreachable unless that variable is set to exactly "1", and the production branches it guards are unchanged.
///
/// It exists so an auditor can stand up a THIRD instance of this app — beside the on-prem filer and the cloud
/// deployment — that serves the real HTTP surface (real routing, real authorization policies, real endpoint filters,
/// real rate limiters, real handlers) while being PHYSICALLY incapable of filing, uploading or deleting anything
/// external. Two guarantees, and they are deliberately inseparable — <see cref="Apply"/> is the only caller of both:
///
///   (1) NO OUTBOUND NETWORK. Every typed HttpClient the composition root registers (Graph/SharePointSink,
///       GraphPatientDirectory, OpenAiClient, DocIntelligencePdf) has its PRIMARY handler replaced by
///       <see cref="BlockedHandler"/>, which throws instead of opening a socket. This is done with ConfigureAll, so
///       it covers every named client including any added later — a new outbound dependency cannot silently escape it.
///   (2) NO CREDENTIAL TRAFFIC. <see cref="TokenCredential"/> is rebound to <see cref="NoNetworkCredential"/>.
///       ClientSecretCredential would otherwise call login.microsoftonline.com on its OWN pipeline, which (1) cannot
///       see. Rebinding it is what makes "no outbound calls" true rather than nearly true.
///
/// The filer seams (IFiler / IDocumentSink) are additionally rebound to <see cref="NullFiler"/> so a filing attempt
/// fails as a named, greppable refusal instead of as a socket error from deep inside SharePointSink.
///
/// The single-instance mutex is handled at the top of Program.cs and is NOT weakened: test mode takes a DIFFERENT,
/// process-scoped mutex name, so it can never acquire, hold, or contend for the production
/// <c>Global\Adelaide.Web</c> filing mutex. A production run never consults this class at all.
/// </summary>
internal static class TestMode
{
    internal const string EnvVar = "ADELAIDE_TEST_MODE";

    /// <summary>True only when ADELAIDE_TEST_MODE is exactly "1".</summary>
    internal static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);

    /// <summary>Thrown instead of performing any outbound call. Deliberately NOT an HttpRequestException: the
    /// standard resilience handler retries those, which would turn one blocked call into a retry storm.</summary>
    internal sealed class OutboundBlockedException(string what)
        : Exception($"ADELAIDE_TEST_MODE: outbound call blocked ({what}). The test host has no network by construction.");

    private sealed class BlockedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            // Report the SCHEME+HOST only — never the path or query, which carry document ids and the KVNR.
            throw new OutboundBlockedException(request.RequestUri is { } u ? $"{u.Scheme}://{u.Host}" : "no-uri");
    }

    private sealed class NoNetworkCredential : TokenCredential
    {
        // A syntactically-valid, expiring, obviously-fake token. Never transmitted: (1) blocks the only client
        // that would carry it. Returning one rather than throwing keeps the failure at the network seam, so a test
        // observes "the app tried to call Graph" instead of "the app failed to authenticate".
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-mode-not-a-real-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    /// <summary>Rebind the outbound seams. Called from the composition root ONLY when <see cref="Enabled"/>.</summary>
    internal static void Apply(WebApplicationBuilder builder)
    {
        BlockOutboundNetwork(builder);

        // (3) the filer seams — last registration wins, so these replace the SharePointSink bindings above.
        builder.Services.AddTransient<Adelaide.SharePoint.IFiler>(_ => new NullFiler());
        builder.Services.AddTransient<Adelaide.Core.IDocumentSink>(_ => new NullFiler());
    }

    /// <summary>Guarantees (1) + (2): every typed HttpClient — present and future — loses its socket, and the credential
    /// pipeline (which does not go through HttpClientFactory) cannot mint a real token. Shared with <see cref="DemoMode"/>.</summary>
    internal static void BlockOutboundNetwork(WebApplicationBuilder builder)
    {
        // (1) every typed client — present and future — loses its socket.
        builder.Services.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new BlockedHandler()));

        // (2) the credential pipeline, which does not go through (1).
        builder.Services.AddSingleton<TokenCredential>(_ => new NoNetworkCredential());
    }

    /// <summary>
    /// A filer/sink that refuses, loudly and locally. Any test that reaches it has found a filing attempt — which is
    /// itself a useful observation, so the refusal names the method rather than failing silently.
    /// <para>
    /// NOTE the deliberate asymmetry on <see cref="ExistsByIdAsync"/>: it THROWS rather than returning false. The
    /// verified-purge core deletes local inputs only when existence is CONFIRMED, and its documented contract is
    /// "other errors THROW (never delete on doubt)". Returning false would be a lie that could make a sweep believe
    /// a filed item had vanished; throwing keeps the fail-safe direction the production contract already relies on.
    /// </para>
    /// </summary>
    private sealed class NullFiler : Adelaide.SharePoint.IFiler, Adelaide.Core.IDocumentSink
    {
        public Task<Adelaide.SharePoint.FilingResult> FileAsync(Adelaide.SharePoint.FilingSpec spec, CancellationToken ct = default) =>
            throw new OutboundBlockedException("IFiler.FileAsync");

        public Task FileAsync(Adelaide.Core.IncomingDocument doc, Adelaide.Core.DocumentUnit unit,
            Adelaide.Core.ProcessingResult result, CancellationToken ct = default) =>
            throw new OutboundBlockedException("IDocumentSink.FileAsync");

        public Task<bool> ExistsByIdAsync(string driveItemId, Adelaide.SharePoint.FilingTarget? target = null, CancellationToken ct = default) =>
            throw new OutboundBlockedException("IFiler.ExistsByIdAsync");

        public Task<bool> TryDeleteByIdAsync(string driveItemId, Adelaide.SharePoint.FilingTarget? target = null, CancellationToken ct = default) =>
            throw new OutboundBlockedException("IFiler.TryDeleteByIdAsync");
    }
}

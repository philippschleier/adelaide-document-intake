using System.Globalization;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Adelaide.Ai;
using Adelaide.Audit;
using Adelaide.Core;
using Adelaide.SharePoint;
using Adelaide.Web;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Http.Resilience;
using Serilog;

// Single-instance guard for the WHOLE web host. FilingClaims (the doc/sha/key claims that serialise filing) are
// PROCESS-LOCAL, so two web hosts on one machine — e.g. the overlap window of a routine rolling deploy — have NO
// cross-process guard and could both file a document. Refuse to start a second instance; the mutex is held for the
// process lifetime and released on shutdown (same idiom the drain uses). Trade-off: a deploy becomes stop-then-start,
// not overlap — a brief restart gap, which is the correct exchange for the single-filer invariant until/unless a
// durable SQLite lease replaces the in-process claim.
//
// TEST MODE (ADELAIDE_TEST_MODE=1) does NOT weaken this guard. It takes a DIFFERENT, process-scoped mutex name, so a
// test host can never acquire, hold, or contend for the production filing mutex — and, being process-scoped, two test
// hosts never block each other either. The production branch is unchanged: an unset (or != "1") variable takes
// Global\Adelaide.Web and refuses exactly as before. See Adelaide.Web.TestMode for the other half (no outbound calls).
var testMode = Adelaide.Web.TestMode.Enabled;
// DEMO MODE (ADELAIDE_DEMO_MODE=1): synthetic corpus + local adapters, no outbound network (see Adelaide.Web.DemoMode).
// It refuses to start beside any production setting, and takes its own mutex name so it never contends with a real filer.
var demoMode = Adelaide.Web.DemoMode.Enabled && !testMode;
if (demoMode) Adelaide.Web.DemoMode.RefuseProductionSettings();
using var hostMutex = new Mutex(true, testMode ? $@"Local\Adelaide.Web.Test.{Environment.ProcessId}" : demoMode ? @"Local\Adelaide.Web.Demo" : @"Global\Adelaide.Web", out var hostMutexCreated);
if (!hostMutexCreated && !testMode && !demoMode)
{
    Console.Error.WriteLine("Adelaide.Web: another instance is already running on this machine — refusing to start a second filer. Stop the other instance first.");
    return;
}

// Optional Key Vault secret bootstrap — pulls configured secrets into the environment BEFORE config is read. No-op +
// .env fallback when ADELAIDE_KEYVAULT_URI is unset, so the current setup is unchanged. See KeyVaultBootstrap.
var kvSummary = await KeyVaultBootstrap.LoadAsync();

var builder = WebApplication.CreateBuilder(args);

// --- Structured logging (Serilog): a rolling JSON-lines file + console. The file survives a restart, so an incident
// stays diagnosable afterwards — grep/jq by DocId, stage, level. Repoint the sink to App Insights / OpenTelemetry at the
// cloud move. PHI must NEVER reach a log (see Adelaide.Core.Redact). ADELAIDE_LOG_DIR overrides the
// location — point it at a volume with room to grow; retention caps the on-disk footprint.
var logDir = Environment.GetEnvironmentVariable("ADELAIDE_LOG_DIR") ?? "logs";
var aiConn = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
builder.Host.UseSerilog((ctx, cfg) =>
{
#pragma warning disable CA1305 // Serilog Console sink's default formatProvider is locale-dependent — cosmetic for console only; the File/AppInsights sinks use CompactJsonFormatter
    cfg
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Polly", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()                                // flows ILogger BeginScope properties (e.g. DocId) onto every line
        .Enrich.WithProperty("app", "Adelaide.Web")
        .Enrich.WithProperty("version", BuildInfo.Version)
        .WriteTo.Console()
        .WriteTo.File(new Serilog.Formatting.Compact.CompactJsonFormatter(), Path.Combine(logDir, "adelaide-.jsonl"),
            rollingInterval: Serilog.RollingInterval.Day, rollOnFileSizeLimit: true, fileSizeLimitBytes: 50_000_000,
            retainedFileCountLimit: 14, shared: true);
#pragma warning restore CA1305
    // Cloud: ship the SAME already-PHI-safe events to App Insights (the redacted events that go to console/file — no raw
    // URLs/bodies). Deliberately NOT OpenTelemetry auto-dependency collection, which would capture raw Graph/OpenAI URLs
    // that carry the KVNR. No-op locally (connection string unset).
    if (!string.IsNullOrWhiteSpace(aiConn))
        cfg.WriteTo.ApplicationInsights(aiConn, new Serilog.Sinks.ApplicationInsights.TelemetryConverters.TraceTelemetryConverter());
});

// --- DI: the same composition as the worker host ---
var sp = demoMode ? SharePointOptions.DemoPlaceholder() : SharePointOptions.Load();
builder.Services.AddSingleton(sp);
builder.Services.AddSingleton<TokenCredential>(_ => new ClientSecretCredential(sp.TenantId, sp.ClientId, sp.ClientSecret));
builder.Services.AddHttpClient<GraphPatientDirectory>().AddStandardResilienceHandler();
builder.Services.AddTransient<IPatientDirectory>(s => s.GetRequiredService<GraphPatientDirectory>());
builder.Services.AddTransient<IPatientSearch>(s => s.GetRequiredService<GraphPatientDirectory>()); // the review screen's patient picker
builder.Services.AddSingleton(demoMode ? AiOptions.DemoPlaceholder() : AiOptions.Load());
builder.Services.AddHttpClient<OpenAiClient>().AddStandardResilienceHandler(o =>
{
    // gpt-4o vision/extraction calls take tens of seconds — the default 10s attempt / 30s total cancels them.
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240); // must be >= 2 x AttemptTimeout
});
builder.Services.AddTransient<IExtractor, AzureOpenAiExtractor>();
builder.Services.AddTransient<ITranscriber, VisionTranscriber>();
builder.Services.AddHttpClient<DocIntelligencePdf>().AddStandardResilienceHandler(o =>
{
    // Searchable-PDF generation on multi-page scans can take tens of seconds; give it the same headroom as vision.
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240);
});
builder.Services.AddTransient<IPdfTextLayer>(sp => sp.GetRequiredService<DocIntelligencePdf>());
builder.Services.AddHttpClient<SharePointSink>().AddStandardResilienceHandler();
builder.Services.AddTransient<IDocumentSink>(s => s.GetRequiredService<SharePointSink>());
builder.Services.AddTransient<IFiler>(s => s.GetRequiredService<SharePointSink>()); // testability seam (P6): resolves to the SAME SharePointSink; lets ProcessAsync/ReconcileAsync take a fake in tests with zero behaviour change
builder.Services.AddTransient<IFiledDocumentStore>(s => s.GetRequiredService<SharePointSink>()); // filer + filed-PDF read-back for the review/viewer endpoints
// Audit substrate: Azure SQL (ledger) when configured (ADELAIDE_SQL_CONNECTION_STRING, or ADELAIDE_SQL_SERVER +
// ADELAIDE_SQL_DATABASE), else local SQLite for dev/small scale. Both implement IAuditStore with identical hash-chain
// semantics, so the seam — and every endpoint — is unchanged by the swap. Keyless: Azure SQL authenticates with the
// App Service managed identity (Active Directory Default).
var auditDb = Environment.GetEnvironmentVariable("ADELAIDE_AUDIT_DB") ?? "adelaide-audit.db";
var sqlConn = AzureSqlAuditStore.ConnectionStringFromEnv();
var auditSubstrate = sqlConn is not null ? "AzureSql" : Path.GetFullPath(auditDb);
builder.Services.AddSingleton<IAuditStore>(_ => sqlConn is not null
    ? new AzureSqlAuditStore(sqlConn)
    : new SqliteAuditStore(auditDb));
builder.Services.AddSingleton<IAuditLog>(s => s.GetRequiredService<IAuditStore>());

// Capture store — ALWAYS registered: every processed document's transcript, model inputs, corpus row and model-call
// records are captured unconditionally (docs/CAPTURE.md). The retention env var no longer gates writes — write-time
// gating is exactly how the corpus starved at 4 rows — it only sizes the deletion window the UploadProcessor drives
// (see TranscriptRetentionGate). SUBSTRATE SELECTION (C3): cloud mode ⇒ the Azure SQL twin — capture.*/measure.*
// schemas in the audit database, keyless, provisioned ONCE by an admin via infra/sql/capture-init.sql — so the
// pre-C3 danger (SQLite defaulting beside a nonexistent audit file on the container's ephemeral disk) no longer
// exists as a default; an explicit ADELAIDE_TRANSCRIPT_DB keeps the SQLite escape hatch with durability
// operator-owned (WARNed at startup in cloud mode). Still deliberately SEPARATE from the append-only ledger:
// deletable/erasable by design. See ITranscriptStore for the contract, TranscriptStore for the store rationale.
var transcriptDbEnv = Environment.GetEnvironmentVariable("ADELAIDE_TRANSCRIPT_DB");
var (sqlCapture, captureSqlitePath) = TranscriptRetentionGate.SelectSubstrate(cloudMode: sqlConn is not null,
    transcriptDbEnv, auditDb);
var captureSubstrate = sqlCapture ? "AzureSql" : Path.GetFullPath(captureSqlitePath!);
// C1 restored, narrowly: in a container a file-path capture store is ephemeral, and the loss is silent (fail-open
// writes). Fatal HERE — before anything is built or served — so the misconfiguration is a named deploy failure
// instead of a store that quietly empties on every restart. Scope, precisely: it catches the SQL settings being
// ABSENT (unset/blank/misspelled setting NAME); a mistyped server VALUE still builds a valid connection string and
// is a later SQL error, not this. See TranscriptRetentionGate.DurabilityError.
if (TranscriptRetentionGate.DurabilityError(
        TranscriptRetentionGate.InContainer(Environment.GetEnvironmentVariable("ADELAIDE_CONTAINER"),
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER")),
        sqlCapture, captureSubstrate, transcriptDbEnv) is { } durabilityError)
    throw new CaptureDurabilityException(durabilityError);
builder.Services.AddSingleton<Adelaide.Audit.ITranscriptStore>(_ => sqlCapture
    ? new Adelaide.Audit.AzureSqlTranscriptStore(sqlConn!)
    : new Adelaide.Audit.TranscriptStore(captureSqlitePath!));
builder.Services.AddSingleton<CaptureHealth>(); // fail-open-but-counted capture failures; surfaced on /api/stats
builder.Services.AddSingleton<SweepHealth>();   // periodic-sweep heartbeat: a healthy idle sweep must not look like a dead one
builder.Services.AddSingleton<Adelaide.Core.IModelCallRecorder, StoreCallRecorder>(); // the wrappers' capture seam → model_calls

// FINDING 1d: ingest malware scanner (ClamAV/clamd sidecar). Configured-or-off: no ADELAIDE_CLAMAV_HOST → disabled
// (no behaviour change for the current deployment); a host → fail-closed Enforce by default. See MalwareScanning.
builder.Services.AddSingleton<IMalwareScanner>(sp => MalwareScanning.Build(sp.GetRequiredService<ILoggerFactory>()));

// FINDING 4: rate-limit the EXPENSIVE endpoints (each upload/retry/reprocess drives paid OCR/LLM + native rendering).
// Partition per authenticated user (Entra oid), falling back to the client IP for any unauthenticated edge; over the
// limit → 429. Generous enough that a human reviewer's normal batch never trips it, low enough to stop runaway automation.
var rateLimitPerMinute = int.TryParse(Environment.GetEnvironmentVariable("ADELAIDE_RATE_LIMIT_PER_MINUTE"), out var rl) && rl > 0 ? rl : 30;
// The reviewer-facing READ endpoints (per-document history) get their OWN, higher budget: they drive no paid work, so
// they must not eat the upload/OCR allowance — but they are enumerable, so they are not unlimited either.
var readLimitPerMinute = int.TryParse(Environment.GetEnvironmentVariable("ADELAIDE_READ_RATE_LIMIT_PER_MINUTE"), out var rrl) && rrl > 0 ? rrl : 120;
// Partition per authenticated user (Entra oid), falling back to the client IP for any unauthenticated edge.
static string RateLimitKey(HttpContext http) =>
    http.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
    ?? http.User.FindFirst("oid")?.Value
    ?? http.Connection.RemoteIpAddress?.ToString()
    ?? "anon";
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("expensive", http =>
        RateLimitPartition.GetFixedWindowLimiter(RateLimitKey(http), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0, // no queue: shed immediately past the limit rather than holding request threads
        }));
    o.AddPolicy("read", http =>
        RateLimitPartition.GetFixedWindowLimiter(RateLimitKey(http), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = readLimitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

// In-process job queue + background worker: /upload enqueues and returns instantly; processing runs independently
// of the browser (closing the tab is safe). The audit log is the durable source the dashboard polls via /documents.
builder.Services.AddSingleton(Channel.CreateUnbounded<UploadJob>());
builder.Services.AddSingleton<DocGate>(); // brief mutual exclusion for the discard/process DuplicateSuspect endpoints
builder.Services.AddSingleton<KeyGate>(); // brief mutual exclusion for the SHA dedup at upload
builder.Services.AddSingleton<FilingClaims>(); // non-blocking per-document/business-key claim for the slow filing paths
builder.Services.AddMemoryCache(o => o.SizeLimit = 240); // bounds the page viewer's caches: filed-PDF bytes + rendered page images (each Size=1, short TTL)
// Explicit factory: the capture store is now always registered, but GetService (not GetRequiredService) keeps
// "absent" a working case for tests that construct the processor without one — processing must survive capture.
builder.Services.AddHostedService(sp => new UploadProcessor(
    sp.GetRequiredService<Channel<UploadJob>>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<IAuditLog>(),
    sp.GetRequiredService<IAuditStore>(),
    sp.GetRequiredService<FilingClaims>(),
    sp.GetRequiredService<ILogger<UploadProcessor>>(),
    sp.GetService<Adelaide.Audit.ITranscriptStore>(),
    sp.GetService<CaptureHealth>(),
    sp.GetRequiredService<SweepHealth>()));

// --- Drain channel auth (Entra, app-only). A dedicated service principal (the on-prem scanner drain) calls
// /api/drain/upload with a token carrying the "Drain" app role; the channel is derived from the VALIDATED token,
// never a client-asserted header. Configured-or-closed: with no tenant/audience set, the drain endpoint refuses (503)
// and the interactive /api/upload is unaffected (the browser uploader's own SSO is a separate, later track).
var drainTenant = Environment.GetEnvironmentVariable("ADELAIDE_DRAIN_AUTH_TENANT");
var drainAudience = Environment.GetEnvironmentVariable("ADELAIDE_DRAIN_AUTH_AUDIENCE");
var drainRole = Environment.GetEnvironmentVariable("ADELAIDE_DRAIN_ROLE") ?? "Drain";
var drainAuthConfigured = !string.IsNullOrWhiteSpace(drainTenant) && !string.IsNullOrWhiteSpace(drainAudience);

// --- Authentication: two schemes, picked per endpoint by the authorization policy. ---
//  • EasyAuth (default) — interactive humans. App Service Easy Auth terminates the Entra OIDC flow at the platform edge
//    and forwards the validated identity (incl. app-role claims) in X-MS-CLIENT-PRINCIPAL; EasyAuthHandler reads it into
//    HttpContext.User. No token is validated in-process (the platform did, and the header is platform-injected). Non-
//    members never reach here: "assignment required" on the Entra app + Easy Auth "require authentication" block them at
//    the edge. The in-app policies below add the Reviewer/Admin split + fail-closed actor-stamping (defence in depth).
//  • Bearer (JwtBearer) — the machine drain (app-only token, "Drain" app role). Unchanged, still configured-or-closed.
var authBuilder = builder.Services
    .AddAuthentication(EasyAuthDefaults.Scheme)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, EasyAuthHandler>(EasyAuthDefaults.Scheme, null);
if (drainAuthConfigured)
{
    authBuilder.AddJwtBearer("Bearer", o =>   // "Bearer" = JwtBearerDefaults.AuthenticationScheme
    {
        o.Authority = $"https://login.microsoftonline.com/{drainTenant}/v2.0";
        o.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudiences = new[] { drainAudience!, $"api://{drainAudience}" },
            ValidateIssuer = true,
            ValidIssuers = new[] { $"https://login.microsoftonline.com/{drainTenant}/v2.0", $"https://sts.windows.net/{drainTenant}/" },
            ValidateLifetime = true,
            RoleClaimType = "roles",   // Entra app roles arrive in the "roles" claim → the Drain policy checks it
        };
    });
}

// --- Authorization: secure-by-default. The FALLBACK policy requires an authenticated MEMBER (Reviewer or Admin, via the
// EasyAuth scheme) for EVERY endpoint that does not opt out — so a newly-added /api route is gated by default rather than
// by remembering to annotate it (the failure mode that left every human endpoint open until now). Exemptions are
// explicit: /health + /version + the SPA shell are .AllowAnonymous(); Admin-only actions add RequireAuthorization("Admin");
// the machine drain uses its own "Drain" policy on the Bearer scheme so it never falls through to the human scheme.
// FINDING 6 (documented design decision — NOT a defect, deliberately no per-patient segmentation): authorization is
// ROLE-based (Reviewer/Admin), not per-object/per-patient. Any authenticated reviewer may open any document or patient
// by id — deliberate for a CENTRAL intake team that triages the whole queue; per-patient/per-site scoping is out of
// scope. Document ids are unguessable (a GUID fragment), but access does NOT rely on that: every /api/* object access is
// gated by the fallback policy below, so a caller without a role is refused whether or not an id leaks. If clinics are
// ever siloed (a reviewer scoped to one home-site), THIS is the seam where a per-object authorization check would live.
var authz = builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(EasyAuthDefaults.Scheme)
        .RequireRole(EasyAuthDefaults.ReviewerRole, EasyAuthDefaults.AdminRole)
        .Build())
    .AddPolicy("Admin", p =>
    {
        p.AddAuthenticationSchemes(EasyAuthDefaults.Scheme);
        p.RequireRole(EasyAuthDefaults.AdminRole);
    });
if (drainAuthConfigured)
{
    // Authorize on the app-role claim directly — robust against claim-type mapping differences between token versions:
    // app-only tokens carry app roles in "roles"; some pipelines remap that to ClaimTypes.Role. Accept either. The Bearer
    // scheme is named explicitly so this machine policy never authenticates against the human EasyAuth scheme.
    authz.AddPolicy("Drain", p =>
    {
        p.AddAuthenticationSchemes("Bearer");
        p.RequireAssertion(ctx => ctx.User.Claims.Any(c => (c.Type == "roles" || c.Type == System.Security.Claims.ClaimTypes.Role) && c.Value == drainRole));
    });
}

// --- Upload limits (FINDING 1a): one ceiling BOTH entry points (/api/upload, /api/drain/upload) inherit. A scanned or
// faxed medical PDF is a few MB; these bound abuse without impeding real intake. The Kestrel body cap rejects an
// oversize REQUEST before it is buffered; MultipartBodyLengthLimit bounds the multipart section; the per-file size +
// count + PDF-signature checks in the endpoints turn a raw 413 / oversized-or-wrong-type blob into a clean, auditable
// rejection at the trust boundary (before anything is written to disk, rendered by the native PDF engine, or filed).
const long MaxUploadRequestBytes = 60L * 1024 * 1024; // hard ceiling for the whole request (all files together)
const long MaxUploadFileBytes = 25L * 1024 * 1024;    // per individual file
const int MaxFilesPerRequest = 20;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadRequestBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadRequestBytes);

// C7′ — the in-app staging-share poller (configured-or-off; the design rules live in DrainPoller.cs). Registered
// always so the health gauge exists; inert with a stated log line when ADELAIDE_DRAIN_SHARE_URL is unset. The
// T-0 intake switch of the migration plan IS this configuration.
builder.Services.AddSingleton<DrainPollerHealth>();
var (drainPollerOptions, drainPollerConfigError) = DrainPollerOptions.FromEnv();
builder.Services.AddHostedService(sp => new DrainPoller(drainPollerOptions, drainPollerConfigError,
    drainPollerOptions is null ? null : new AzureFilesStagingSource(drainPollerOptions.ShareUrlWithSas, drainPollerOptions.Subdir),
    sp.GetRequiredService<DrainPollerHealth>(), sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<IAuditStore>(),
    sp.GetRequiredService<Channel<UploadJob>>(), sp.GetRequiredService<KeyGate>(), sp.GetRequiredService<IMalwareScanner>(),
    MaxUploadFileBytes, sp.GetRequiredService<ILogger<DrainPoller>>()));

// TEST MODE, second half: rebind every outbound seam so the host physically cannot reach Graph/SharePoint/OpenAI/
// Document Intelligence or an Entra token endpoint. LAST, so it wins over every registration above. No-op in production.
if (testMode) Adelaide.Web.TestMode.Apply(builder);
// Failure-composition harness: opt-in fault seams (a filer that SUCCEEDS, and a nominated append that FAILS), so a
// partial failure can be reached from the direction TestMode alone cannot model. After TestMode.Apply, so the
// succeeding filer wins over NullFiler. No-op unless its own variables are set.
if (testMode) Adelaide.Web.TestFaults.Apply(builder);
// DEMO MODE: rebind the four external seams to the synthetic local adapters (and block outbound network). LAST, so it wins.
if (demoMode) Adelaide.Web.DemoMode.Apply(builder);

var app = builder.Build();

app.UseSerilogRequestLogging(); // structured HTTP access log (method/path/status/elapsed) — no query string or body, so PHI-safe

// FINDING 5: security response headers on EVERY response (SPA shell, API, static assets), set early so nothing escapes
// them. No CORS is configured, so the API stays same-origin. The CSP is tuned for this Angular SPA: script-src 'self'
// (the production build emits only external module scripts — critical-CSS inlining is disabled, so there is no inline
// onload handler to allow); style-src permits 'unsafe-inline' for Angular's runtime-injected component styles (style
// injection, not script execution); blob: is allowed for the sandboxed PDF-pane iframe. frame-ancestors 'none' (+
// X-Frame-Options) blocks clickjacking; object-src 'none' blocks plugins. HSTS only outside Development (never poison
// a localhost https cache). TLS itself is enforced at the platform edge (App Service https_only).
var hstsEnabled = !app.Environment.IsDevelopment();

// KEEP AN UNHANDLED EXCEPTION INSIDE THE PIPELINE. Without this, a throwing handler escapes to Kestrel, which
// synthesises the 500 itself — and that path RESETS the response, discarding every header set below. Measured: an
// unhandled 500 carried only Content-Length/Date/Server, i.e. no CSP, no nosniff, no X-Frame-Options, no HSTS, while
// the same route's 200 and 404 carried all of them. Development already has a handler (WebApplication auto-adds the
// developer exception page), so this is registered only outside it — and only outside it, so the dev stack-trace page
// is preserved for debugging.
//
// The body stays EMPTY: byte-for-byte what Kestrel already produced, so this adds no disclosure surface. It is
// registered BEFORE the header middleware so the header middleware runs inside it.
//
// NOTE — this is half of the fix and does nothing on its own. ExceptionHandlerMiddleware calls Response.Clear(),
// which wipes headers set directly. The headers below therefore have to be applied via OnStarting (see next).
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler(b => b.Run(ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return Task.CompletedTask;
    }));

app.Use(async (ctx, next) =>
{
    // Applied via OnStarting — immediately before headers flush — NOT directly. Setting them directly loses them
    // whenever the response is reset or cleared, which is exactly the unhandled-500 case. The two halves are
    // independent and each is useless alone: measured on an unhandled 500, OnStarting WITHOUT the handler above still
    // lost every header in Production (Kestrel's synthesised error response never fires these callbacks), and the
    // handler WITHOUT OnStarting lost them too (Response.Clear()). Both together is what restores them.
    ctx.Response.OnStarting(static state =>
    {
        var (h, hsts) = ((IHeaderDictionary, bool))state;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self'; " +
            "style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' blob: data:; " +
            "font-src 'self' data:; " +
            "connect-src 'self'; " +
            "frame-src 'self' blob:; " +
            "object-src 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self'; " +
            "frame-ancestors 'none'";
        if (hsts) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        return Task.CompletedTask;
    }, (ctx.Response.Headers, hstsEnabled));
    await next();
});

var inbox = Environment.GetEnvironmentVariable("ADELAIDE_INBOX") ?? "inbox";
Directory.CreateDirectory(inbox);

// One-line startup banner: the resolved runtime state, so nobody — operator or troubleshooting agent — has to PROBE for
// "which build / which DB / which port / is the drain endpoint open / how concurrent" after an incident. It's all here.
app.Logger.LogInformation(
    "Adelaide.Web started version={Version} env={Env} urls={Urls} auditDb={AuditDb} inbox={Inbox} drainAuthConfigured={DrainAuth} concurrency={Concurrency} reconcileMinutes={ReconcileMinutes} logDir={LogDir} keyVault={KeyVault} malwareScan={MalwareScan} testMode={TestMode}",
    BuildInfo.Version, app.Environment.EnvironmentName,
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "(host default)",
    auditSubstrate, Path.GetFullPath(inbox), drainAuthConfigured,
    Environment.GetEnvironmentVariable("ADELAIDE_CONCURRENCY") ?? "4 (default)",
    Environment.GetEnvironmentVariable("ADELAIDE_RECONCILE_MINUTES") ?? "2 (default)",
    Path.GetFullPath(logDir), kvSummary, MalwareScanning.Describe(), testMode);

// TEST MODE must never be silent. It disables the single-filer refusal and blocks every outbound call, so a host that
// somehow starts with ADELAIDE_TEST_MODE=1 would accept HTTP, refuse nothing, and file NOTHING — the most expensive
// possible failure to diagnose from behaviour alone. Announced as an ERROR (not INFO) because on any real deployment
// this line is an incident, and it names both effects so the reader does not have to find this file.
if (testMode)
    app.Logger.LogError("ADELAIDE_TEST_MODE=1 — TEST HOST: the single-instance filing mutex is BYPASSED and every outbound call (Graph/SharePoint/OpenAI/Document Intelligence/Entra) is BLOCKED. This host cannot file, upload or delete anything external. If this is a deployed environment, stop it: nothing is being filed.");

if (demoMode)
    app.Logger.LogWarning("ADELAIDE_DEMO_MODE=1 — DEMO HOST on SYNTHETIC data: transcription/extraction replay demo/fixtures.json, patients come from demo/patients.json, and 'filed' documents are written to {FiledDir}. Every outbound call is BLOCKED; nothing is sent to SharePoint, Azure OpenAI or Document Intelligence.",
        Adelaide.Web.DemoMode.FiledDir());

// Capture semantics changed 2026-08 (docs/CAPTURE.md): model-boundary capture is UNCONDITIONAL; the retention env
// var is now purely the deletion window. Stated at startup — with the store/ledger tallies — so nobody has to infer
// the mode or the capture health from behaviour. Best-effort: a broken store must not block startup.
app.Logger.LogInformation("Capture: {CaptureMode} store={CaptureStore}",
    TranscriptRetentionGate.Describe(), captureSubstrate);
// The escape hatch announces itself whenever it is SET — not only in cloud mode. The old `sqlConn is not null`
// guard meant the one case an operator most needs told (an explicit override that silently won over the intended
// substrate) produced no line at all whenever the SQL connection had not resolved. In the container the same
// configuration is fatal above; here it is the localhost/dev affordance, warned so it is never a surprise.
if (TranscriptRetentionGate.EscapeHatchActive(transcriptDbEnv))
    app.Logger.LogWarning("Capture escape hatch active: ADELAIDE_TRANSCRIPT_DB is set — the SQLite store at {CaptureStore} is operator-owned; its durability is not guarded{CloudNote}.",
        captureSubstrate, sqlConn is not null ? " and it OVERRODE the Azure SQL twin this deployment would otherwise use" : "");
try
{
    var captureStats = await CaptureStats.CollectAsync(app.Services.GetService<Adelaide.Audit.ITranscriptStore>(),
        app.Services.GetRequiredService<IAuditStore>(), app.Services.GetService<CaptureHealth>(), CancellationToken.None);
    app.Logger.LogInformation("Capture health at startup: {@Capture}", captureStats);
}
catch (Exception ex)
{
    app.Logger.LogWarning("Capture health unavailable at startup ({ExType}); capture writes will still be attempted per document.", ex.GetType().Name);
}

// Build identity — answers "is the running code the latest?" without Get-Process gymnastics.
app.MapGet("/version", () => Results.Json(new { version = BuildInfo.Version, startedUtc = BuildInfo.StartedUtc, environment = app.Environment.EnvironmentName })).AllowAnonymous();

// Liveness probe (App Service health_check_path, anonymous). PURE process-liveness: it deliberately does NOT touch the
// audit DB, so a SQL outage cannot drive the platform to evict/restart-loop the single instance (a restart would not fix
// a SQL outage anyway — the process is alive, just degraded), and it leaks nothing to anonymous callers — no state
// histogram, no drain-config flag. The audit-store readability check + the histogram move to the AUTHENTICATED /api/health.
app.MapGet("/health", () =>
    Results.Json(new { status = "ok", version = BuildInfo.Version, startedUtc = BuildInfo.StartedUtc })).AllowAnonymous();

// Operator health/diagnostics — gated by the fallback policy (Reviewer/Admin); no .AllowAnonymous, so the platform Easy
// Auth + the in-app fallback both require a role. Probes the audit store (system of record) and returns the non-PHI state
// histogram + drain-config flag — the "ledger readable, roughly what's queued, drain configured?" view that used to sit on
// the anonymous /health. ALWAYS 200 (a diagnostic, NOT the liveness probe): a SQL outage is reported as degraded in the
// body, never a 503, so only the liveness probe above ever influences a platform restart decision.
app.MapGet("/api/health", async (IAuditStore store, CancellationToken ct) =>
{
    try
    {
        var states = await store.CountsByStateAsync(ct);
        return Results.Json(new { status = "ok", auditReadable = true, version = BuildInfo.Version, startedUtc = BuildInfo.StartedUtc, drainAuthConfigured, states });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Audit store not readable (/api/health)");
        return Results.Json(new { status = "degraded", auditReadable = false, version = BuildInfo.Version, startedUtc = BuildInfo.StartedUtc, drainAuthConfigured, error = Redact.Error(ex) });
    }
});

// Always on now: interactive users authenticate via Easy Auth (the fallback policy gates every non-exempt endpoint),
// and the drain via Bearer when configured. The authn middleware runs EasyAuthHandler per request (no-op when there is
// no platform header and no dev actor → anonymous → the fallback policy challenges).
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // FINDING 4: after auth so the partition key can use the authenticated Entra oid

// --- Angular SPA, single process: serve the built client from this host; /api/* endpoints take precedence and
// unknown routes fall back to index.html so deep links like /review/{id} work. (Dev alternative: ng serve + proxy.)
var clientDist = new[]
{
    Environment.GetEnvironmentVariable("ADELAIDE_CLIENT_DIST"),
    Path.Combine(builder.Environment.ContentRootPath, "..", "Adelaide.Web.Client", "dist", "adelaide-web-client", "browser"),
    Path.Combine(builder.Environment.ContentRootPath, "src", "Adelaide.Web.Client", "dist", "adelaide-web-client", "browser"),
}.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p!)).FirstOrDefault(Directory.Exists);

if (clientDist is not null)
{
    var client = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(clientDist);
    // index.html names the content-hashed JS/CSS bundles, so it must NEVER be cached — a stale copy keeps loading old
    // code after a rebuild (the "I redeployed but still see the old UI" trap). The hashed assets are immutable.
    void SetCache(Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext ctx) =>
        ctx.Context.Response.Headers["Cache-Control"] =
            ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase)
                ? "no-store, must-revalidate"
                : "public, max-age=31536000, immutable";
    app.UseStaticFiles(new StaticFileOptions { FileProvider = client, OnPrepareResponse = SetCache });
    // SPA shell is AllowAnonymous: the platform (Easy Auth require-auth) gates whether the shell is served at all; non-
    // members never reach it. The shell loading for an authenticated member is fine — every /api/* call it makes is gated
    // by the fallback policy, so no data is exposed without a role. Keeping the shell anonymous also keeps local dev simple.
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = client, OnPrepareResponse = SetCache }).AllowAnonymous();
}
else
{
    app.MapGet("/", () => Results.Content(
        "Adelaide API läuft. Angular-Client-Build fehlt — einmal `npm run build` in src/Adelaide.Web.Client ausführen (oder ng serve auf :4200 verwenden).",
        "text/plain; charset=utf-8")).AllowAnonymous();
}

// Capture bytes (no-loss) → hash (exact-duplicate check, before any AI cost) → record receipt → enqueue.
// An exact duplicate is held as DuplicateSuspect for a human decision (compare side-by-side, discard or process).
app.MapPost("/api/upload", async (HttpRequest req, IAuditLog audit, IAuditStore store, Channel<UploadJob> queue, KeyGate keyGate, IMalwareScanner malwareScanner, CancellationToken ct) =>
{
    if (!req.HasFormContentType) return Results.BadRequest("multipart/form-data required");
    var form = await req.ReadFormAsync(ct);
    // FINDING 1a: bound the batch, then validate each file by SIZE and CONTENT (magic bytes) before any is persisted.
    if (form.Files.Count > MaxFilesPerRequest) return Results.BadRequest($"Zu viele Dateien in einer Anfrage (max. {MaxFilesPerRequest}).");
    var actor = ActorOf(req.HttpContext); // the authenticated uploading user — stamped on the Received event
    var jobs = new List<object>();
    foreach (var file in form.Files)
    {
        if (file.Length == 0) continue;
        if (file.Length > MaxUploadFileBytes) return Results.BadRequest($"Eine Datei überschreitet das Größenlimit ({MaxUploadFileBytes / (1024 * 1024)} MB)."); // no filename in the message (may embed a KVNR)
        var docId = Guid.NewGuid().ToString("N")[..12];
        var path = Path.Combine(inbox, $"{docId}.pdf");
        byte[] bytes;
        await using (var ms = new MemoryStream()) { await file.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
        if (!PdfSignature.LooksLikePdf(bytes)) return Results.BadRequest("Nur PDF-Dateien werden akzeptiert."); // content check (magic bytes), not the extension/MIME
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        // FINDING 1d: scan BEFORE persisting/rendering/filing. A blocked upload is recorded (poison → DeadLetter) but its
        // bytes are never written and it is never enqueued, so nothing malicious reaches the renderer or SharePoint.
        var scan = await malwareScanner.ScanAsync(bytes, ct);
        if (!scan.Allowed)
        {
            await audit.AppendAsync(new AuditEntry(docId, "Received", actor, Details: J(new { channel = "Upload", sha256 = sha }),
                Projection: new ProjectionOverrides(OriginalName: file.FileName)), ct);
            await audit.AppendAsync(new AuditEntry(docId, "DeadLetter", "malware-scan", Details: J(new { errorClass = ReasonCodes.MalwareBlocked, reasonCodes = new[] { ReasonCodes.MalwareBlocked } }),
                Projection: new ProjectionOverrides(Reasons: scan.Reason)), ct);
            app.Logger.LogWarning("Upload blocked by malware scan for {DocId} (verdict={Verdict}); not persisted, not enqueued.", docId, scan.Verdict);
            jobs.Add(new { documentId = docId, file = file.FileName, rejected = scan.Reason });
            continue;
        }
        await File.WriteAllBytesAsync(path, bytes, ct);
        // Serialise the exact-duplicate check + receipt per content hash, so two users uploading the SAME bytes at the
        // same moment can't both miss the dedup and both enter the pipeline (TOCTOU). Different files hash differently → no contention.
        using (await keyGate.LockAsync("sha:" + sha, ct))
        {
            var existing = await store.FindBySha256Async(sha, docId, ct);
            await audit.AppendAsync(new AuditEntry(docId, "Received", actor, Details: J(new { channel = "Upload", sha256 = sha }),
                Projection: new ProjectionOverrides(OriginalName: file.FileName)), ct);
            if (existing is not null)
            {
                // C6: the event names the duplicate by DOCUMENT ID only; the reviewer-facing prose (which quotes the
                // other file's name) is projection-only. The compare view resolves both names by id anyway.
                await audit.AppendAsync(new AuditEntry(docId, "DuplicateSuspect", "dedup", Details: J(new
                {
                    duplicateOf = existing.DocumentId,
                    reasonCodes = new[] { ReasonCodes.DuplicateSuspect },
                }), Projection: new ProjectionOverrides(Reasons: $"inhaltsgleich (SHA-256) mit „{existing.OriginalName ?? existing.DocumentId}“")), ct);
                jobs.Add(new { documentId = docId, file = file.FileName, duplicateOf = existing.DocumentId });
                continue; // no pipeline run until a human decides
            }
            await queue.Writer.WriteAsync(new UploadJob(docId, path, file.FileName), ct);
            jobs.Add(new { documentId = docId, file = file.FileName });
        }
    }
    return Results.Json(new { jobs });
}).RequireCsrfHeader().RequireRateLimiting("expensive");

// Drain channel (at-least-once, app-only auth): the on-prem scanner drain posts ONE file per request carrying the
// "Drain" app role. Idempotency is keyed on the origin's immutable SourceRecordId (X-Source-Record-Id) — a re-delivery
// of the same source file is skipped, while two distinct files that share bytes BOTH process (no content-collision
// suppression). channel="Drain" and the actor come from the VALIDATED token, never the request body.
var drainUpload = app.MapPost("/api/drain/upload", async (HttpRequest req, IAuditLog audit, IAuditStore store, Channel<UploadJob> queue, KeyGate keyGate, IMalwareScanner malwareScanner, CancellationToken ct) =>
{
    if (!drainAuthConfigured) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (!req.HasFormContentType) return Results.BadRequest("multipart/form-data required");
    // The drain PERCENT-ENCODES this header: the record-id (share-relative path) can contain non-ASCII characters (German
    // umlauts in the filename) and HTTP header values must be ASCII. Decode it back to the original path so the idempotency
    // key + audit record match the source. UnescapeDataString is a no-op on a plain-ASCII value, so a legacy un-encoded
    // sender still works. (Before this, the drain crashed client-side on umlaut names and silently dead-lettered the doc.)
    var srid = Uri.UnescapeDataString(req.Headers["X-Source-Record-Id"].ToString());
    if (string.IsNullOrWhiteSpace(srid)) return Results.BadRequest("X-Source-Record-Id header required");
    var form = await req.ReadFormAsync(ct);
    if (form.Files.Count != 1) return Results.BadRequest("exactly one file per drain upload");
    var file = form.Files[0];
    if (file.Length == 0) return Results.BadRequest("empty file");
    if (file.Length > MaxUploadFileBytes) return Results.BadRequest("file too large"); // FINDING 1a: per-file size cap
    var actor = "drain:" + (req.HttpContext.User.FindFirst("oid")?.Value
        ?? req.HttpContext.User.FindFirst("azp")?.Value
        ?? req.HttpContext.User.FindFirst("appid")?.Value ?? "unknown");

    // Read the incoming bytes ONCE; everything downstream — content-first idempotency (verify-hash-before-skip),
    // per-srid TOCTOU lock, path-reuse handling, scan-before-persist, capture + enqueue — is the SHARED ingestion
    // core (DrainIngest, C7′) the in-app staging-share poller uses too, so the two intake paths can never drift.
    // recordRejections=false: this CLIENT keeps the file and its own ledger, so invalid input returns as an HTTP
    // error without a document record — exactly the previous endpoint behavior (malware still records poison).
    byte[] bytes;
    await using (var ms = new MemoryStream()) { await file.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
    var result = await DrainIngest.IngestAsync(bytes, file.FileName, srid, actor, recordRejections: false, inbox,
        audit, store, queue, keyGate, malwareScanner, MaxUploadFileBytes, app.Logger, ct);
    return result.Kind switch
    {
        DrainIngest.ResultKind.Deduplicated =>
            Results.Json(new { jobs = new[] { new { documentId = result.DocumentId, file = file.FileName, sourceRecordId = srid, deduplicated = true } } }),
        DrainIngest.ResultKind.Ingested =>
            Results.Json(new { jobs = new[] { new { documentId = result.DocumentId, file = file.FileName, sourceRecordId = srid } } }),
        // 422 tells the drain client this record is rejected (do not re-deliver); the poison record exists.
        DrainIngest.ResultKind.RejectedRecorded =>
            Results.UnprocessableEntity(new { documentId = result.DocumentId, sourceRecordId = srid, rejected = result.Reason }),
        _ => Results.BadRequest(result.Reason),
    };
});
if (drainAuthConfigured) drainUpload.RequireAuthorization("Drain");

// Resolve a duplicate as CONFIRMED: discard (audited) — or process anyway (e.g. a legitimate re-issue). Accepts BOTH
// duplicate layers: exact-byte suspects (DuplicateSuspect) AND business-key flags (NeedsReview carrying duplicateOf).
// GUARD PINNED to duplicateOf-present — a bare NeedsReview (low_confidence, unclassified, …) is NEVER one-click
// discardable.
app.MapPost("/api/duplicate-discard", async (string id, HttpContext http, IAuditLog audit, DocGate docGate, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest();
    using var docLock = await docGate.LockAsync(id, ct); // mutually exclusive with duplicate-process: one decides before the other re-reads State
    var doc = await audit.GetDocumentAsync(id, ct);
    if (doc is null) return Results.NotFound();
    if (!(doc.State == "DuplicateSuspect" || (doc.State == "NeedsReview" && !string.IsNullOrEmpty(doc.DuplicateOf))))
        return Results.BadRequest("Nur Duplikat-Verdachtsfälle können verworfen werden.");
    // The endpoint APPENDS ONLY — a discard is reversible (undiscard) until the recovery window lapses, so it must
    // not destroy its own artifacts. Artifact release belongs to the aged discard sweep alone (sole deletion
    // authority; ships with the sweep commit — under the purge-off posture nothing was deleted here anyway). Stale
    // review tabs are no longer this delete's problem: /api/review and /api/split 409 on every settled state.
    await audit.AppendAsync(new AuditEntry(id, "Discarded", ActorOf(http), Details: J(new
    {
        duplicateOf = doc.DuplicateOf,
    }), Projection: new ProjectionOverrides(Reasons: "als Duplikat bestätigt und verworfen")), ct);
    return Results.Json(new { ok = true });
}).RequireCsrfHeader();

app.MapPost("/api/duplicate-process", async (string id, HttpContext http, IAuditLog audit, Channel<UploadJob> queue, DocGate docGate, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest();
    using var docLock = await docGate.LockAsync(id, ct); // mutually exclusive with duplicate-discard: one decides before the other re-reads State (formerly this also closed a discard-deletes-while-processing race; the inline delete is retired — the sweep is the sole deleter)
    var doc = await audit.GetDocumentAsync(id, ct);
    if (doc is null) return Results.NotFound();
    if (doc.State != "DuplicateSuspect") return Results.BadRequest("Dokument ist kein Duplikat-Verdachtsfall.");
    var path = Path.GetFullPath(Path.Combine(inbox, $"{id}.pdf"));
    if (!File.Exists(path)) return Results.NotFound("Original-PDF liegt nicht mehr vor — bitte erneut hochladen.");
    // C6: no name in the payload (the projection keeps its OriginalName via COALESCE); Reasons "" clears the stale
    // duplicate-suspect prose exactly as the old empty reasons array did.
    await audit.AppendAsync(new AuditEntry(id, "Received", ActorOf(http), Details: J(new
    {
        channel = "DuplicateOverride",
    }), Projection: new ProjectionOverrides(Reasons: "")), CancellationToken.None);
    await queue.Writer.WriteAsync(new UploadJob(id, path, doc.OriginalName ?? $"{id}.pdf"), CancellationToken.None);
    return Results.Json(new { ok = true });
}).RequireCsrfHeader();

// UNDISCARD (discard-recovery): the guarded exit from Discarded — the reverse of /api/duplicate-discard, in the
// /api/reprocess mold (a settled state with a sanctioned, guarded exit). Window policy lives HERE in the command
// handler (wall clock, injectable via UndiscardGuard) — never in a reducer; expiry becomes state only via the
// sweep's Purged append. The restore is ONE appended event whose Type is the pre-discard state — derived by
// scanning back from the LATEST Discarded event into a whitelist (UndiscardGuard) — so the projection flips by
// the ordinary latest-advancing-event rule and the document id never changes. Details stay structural (C6); the
// reviewer's free-text reason goes to the erasable projection Reasons, tied to this event by a handler-minted
// correlation id (Seq is store-assigned and unknowable at append time; the ledger's partial unique index makes a
// collision fail the append loudly instead of silently attaching one reason to two events). ReasonCodes,
// Confidence, DuplicateOf and Sha256 survive via COALESCE — the document returns to the decision point it left.
app.MapPost("/api/undiscard", async (string id, UndiscardBody body, HttpContext http, IAuditLog audit, FilingClaims claims, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest();
    var reason = body?.Reason?.Trim();
    if (string.IsNullOrEmpty(reason)) return Results.BadRequest("Bitte einen Grund für die Wiederherstellung angeben.");
    if (reason.Length > 500) reason = reason[..500]; // the projection Reasons column is one display string, not an archive
    // Claim, not DocGate: the claim key is what the sweep, the worker, review submit and /api/reprocess all hold,
    // so contention with ANY live path (above all a sweep mid-delete) is a clean 409 instead of a race.
    using var claim = claims.Acquire("doc:" + id);
    if (!claim.Acquired) return Results.Conflict("Dokument wird gerade verarbeitet — bitte kurz erneut versuchen.");
    var doc = await audit.GetDocumentAsync(id, ct);
    if (doc is null) return Results.NotFound();
    if (doc.State != "Discarded")
        return Results.Conflict(DocumentStates.IsSettled(doc.State)
            ? TerminalConflictText(doc.State)   // incl. the Purged arm: window over, artifacts released, sealed
            : "Nur verworfene Dokumente können wiederhergestellt werden.");
    var decision = UndiscardGuard.Evaluate(await audit.GetEventsAsync(id, ct), DateTimeOffset.UtcNow, DiscardPurgeGrace.Days,
        pdfExists: File.Exists(Path.GetFullPath(Path.Combine(inbox, $"{id}.pdf"))),
        sidecarExists: File.Exists(Path.GetFullPath(Path.Combine(inbox, $"{id}.review.json"))));
    switch (decision.Outcome)
    {
        case UndiscardGuard.Result.NotDiscarded:        // stream/projection disagreement — unreachable under the claim
        case UndiscardGuard.Result.UnknownPredecessor:  // no restorable pre-discard state (e.g. a seeded lone Discarded event)
            return Results.Conflict("Wiederherstellung nicht möglich: kein wiederherstellbarer Vorzustand.");
        case UndiscardGuard.Result.WindowExpired:
            return Results.Conflict("Wiederherstellung nicht möglich: die Wiederherstellungsfrist ist abgelaufen.");
        case UndiscardGuard.Result.PdfMissing:
            return Results.Conflict("Wiederherstellung nicht möglich: Original-PDF liegt nicht mehr vor.");
        case UndiscardGuard.Result.SidecarMissing:
            return Results.Conflict("Wiederherstellung nicht möglich: die Prüfdaten (Review-Sidecar) fehlen.");
    }
    var actor = ActorOf(http);
    var corr = Guid.NewGuid().ToString("N");
    await audit.AppendAsync(new AuditEntry(id, decision.Predecessor!, actor,
        CorrelationId: corr,
        Details: J(new { channel = "Undiscard", from = "Discarded", to = decision.Predecessor, actor }),
        Projection: new ProjectionOverrides(Reasons: $"wiederhergestellt [{corr[..8]}]: {reason}")), ct);
    // The ONLY rate signal for mistaken discards that will ever exist (the ledger is countable retroactively
    // by Type+channel, but nothing else measures it live). Structural fields only — never the reason prose.
    app.Logger.LogInformation("Undiscarded {DocId}: Discarded -> {RestoredTo} by {Actor}, {ElapsedDays:F1}d after the discard.",
        id, decision.Predecessor, actor, (DateTimeOffset.UtcNow - decision.DiscardedAt!.Value).TotalDays);
    return Results.Json(new { ok = true, restoredTo = decision.Predecessor });
}).RequireCsrfHeader();

// Header data for the side-by-side compare view. Per-side `available`: intake copy present OR the filed item still
// resolvable in SharePoint — false ONLY on a CONFIRMED absence (404), so the UI's "nicht gefunden (evtl. gelöscht)"
// placeholder never shows on a mere Graph hiccup (uncertainty → true; the viewer fetch then surfaces the real error).
app.MapGet("/api/compare", async (string? id, string? other, IAuditLog audit, IFiledDocumentStore sink, SharePointOptions spOpt, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest();
    if (string.IsNullOrEmpty(other) || !other.All(char.IsAsciiLetterOrDigit)) return Results.BadRequest();
    var a = await audit.GetDocumentAsync(id, ct);
    var b = await audit.GetDocumentAsync(other, ct);
    if (a is null || b is null) return Results.NotFound();
    async Task<bool> AvailableAsync(DocumentAudit d)
    {
        if (File.Exists(Path.GetFullPath(Path.Combine(inbox, $"{d.DocumentId}.pdf")))) return true;
        var locator = FiledLocator.Require(d, "api/compare", app.Logger);
        if (locator is null) return false; // never-filed (no id, no intake) or the tripwire fired → show the placeholder
        try { return await sink.ExistsByIdAsync(locator, await UploadProcessor.RecordedFiledTargetAsync(audit, spOpt, d.DocumentId, ct), ct); } // probe the drive the locator was recorded in (C-2)
        catch { return true; } // Graph uncertain → do NOT claim "deleted"
    }
    // hasLocator: the doc carries a durable driveItemId → supersede can delete it by id. A Filed doc without one is a
    // legacy pre-backfill item; the compare screen disables "Vorhandenes ersetzen" for it with a backfill hint.
    // savedRotation: each side renders at its own saved angle (client passes explicit rot). A filed side has no inbox PDF
    // → 0, which is correct — its /Rotate is baked into the filed artifact and honoured by the raster path.
    object Row(DocumentAudit d, bool available) => new { documentId = d.DocumentId, original = d.OriginalName, state = d.State, contentType = d.ContentType, lastUpdated = d.LastUpdated, duplicateOf = d.DuplicateOf, available, hasLocator = !string.IsNullOrEmpty(d.DriveItemId), savedRotation = UploadProcessor.ReadSavedRotationAngle(Path.GetFullPath(Path.Combine(inbox, $"{d.DocumentId}.pdf"))) };
    return Results.Json(new { a = Row(a, await AvailableAsync(a)), b = Row(b, await AvailableAsync(b)) });
});

// Visual page diff (PNG): matching content faded gray, differences red. Each side resolves like /api/file: the
// captured intake original when present, else the FILED doc's PDF fetched from SharePoint into a TRANSIENT temp file
// (PdfDiff renders from paths) that is deleted before the response returns — no lingering second PHI copy.
app.MapGet("/api/diff", async (string? a, string? b, IAuditStore store, IFiledDocumentStore sink, SharePointOptions spOpt, CancellationToken ct, int page = 1) =>
{
    if (string.IsNullOrEmpty(a) || !a.All(char.IsAsciiLetterOrDigit)) return Results.BadRequest();
    if (string.IsNullOrEmpty(b) || !b.All(char.IsAsciiLetterOrDigit)) return Results.BadRequest();
    page = Math.Clamp(page, 1, 30);
    var temps = new List<string>();
    try
    {
        var pa = await ResolveAsync(a);
        var pb = await ResolveAsync(b);
        if (pa is null || pb is null) return Results.NotFound();
        var png = PdfDiff.DiffPng(pa, pb, page);
        return png is null ? Results.NotFound() : Results.File(png, "image/png");
    }
    finally { foreach (var t in temps) try { File.Delete(t); } catch { /* best effort — temp dir */ } }

    async Task<string?> ResolveAsync(string docId)
    {
        var p = Path.GetFullPath(Path.Combine(inbox, $"{docId}.pdf"));
        if (File.Exists(p)) return p;
        var doc = await store.GetDocumentAsync(docId, ct);
        if (doc is null) return null;
        var locator = FiledLocator.Require(doc, "api/diff", app.Logger);
        if (locator is null) return null; // tripwire logged; diff pane 404s → not-available
        var bytes = await sink.DownloadFiledPdfAsync(locator, await UploadProcessor.RecordedFiledTargetAsync(store, spOpt, docId, ct), ct); // fetch from the drive the locator was recorded in (C-2)
        if (bytes is null) return null;
        var tmp = Path.Combine(Path.GetTempPath(), $"adelaide-diff-{docId}-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        temps.Add(tmp);
        return tmp;
    }
});

// Read-only PAGE THUMBNAIL: renders ONE page of an in-review doc's inbox PDF to a small JPEG, for the split-mode
// thumbnail rail (a reviewer needs to see the pages of a 20–30-page fax stack to place cuts). A pure view helper — NO
// filing/classification/patient logic, no state change. Inbox docs only (splitting happens in review); 404 otherwise.
// Gated by the fallback auth policy like /api/file (a GET the authenticated SPA makes); no-store (PHI page image).
// Read-only PAGE IMAGE for the shared page viewer (review / split / compare / enlarge modal): renders ONE page of a
// doc's PDF to a JPEG (small for rail tiles, larger for the reader via `w`). Resolves the intake copy OR the FILED PDF
// (downloaded + briefly cached so N page renders don't each re-download). Orientation via the SAME WithRotations path for
// every surface (so they can't disagree): `rot` (0/90/180/270) is EXPLICIT — the CLIENT always passes it (0 when
// unrotated), sourced from the doc's savedRotation on a payload it already loaded. That makes this URL DETERMINISTIC
// (same URL ⇒ same bytes forever), which is what keeps every cache layer correct — including the browser's in-memory
// image cache. (A server-side "default to the saved angle" makes one URL return different bytes over time and defeats
// them all, so the angle lives in the URL, never as a hidden server default.) Pure view helper, no state change; gated
// by the fallback policy like /api/file. The "server-rasterized page image" keeps the raw PDF out of the browser.
app.MapGet("/api/page", async (string id, int page, HttpContext http, IAuditStore store, IFiledDocumentStore sink, SharePointOptions spOpt, IMemoryCache cache, CancellationToken ct, int w = 220, int rot = 0) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest();
    if (page < 1 || page > 400) return Results.BadRequest();
    w = Math.Clamp(w, 40, 2400); // up to 2400 so zooming IN re-renders at higher DPI (real detail) rather than upscaling
    rot = ((rot % 360) + 360) % 360; // explicit, client-supplied (0 when unrotated) — the URL fully determines the bytes
    http.Response.Headers.CacheControl = "no-store"; // patient page image — never in the browser disk cache
    // Bounded server cache of the RENDERED page (keyed by id/page/width/rotation): the WebP encode is ~150–400ms and the
    // sliding-window viewer releases + re-fetches pages, so scroll-back / re-open would otherwise re-render every time.
    // rot is part of the key, so a re-orientation is a distinct entry. Short TTL + size cap keep the PHI footprint bounded.
    var imgKey = $"pageimg:{id}:{page}:{w}:{rot}";
    if (cache.TryGetValue(imgKey, out byte[]? cachedImg) && cachedImg is not null)
        return Results.File(cachedImg, "image/webp");
    var bytes = await ResolvePdfBytesAsync(id, inbox, store, sink, spOpt, cache, app.Logger, ct);
    if (bytes is null) return Results.NotFound();
    try
    {
        // Resolution scales with the requested width: a zoomed-in / HiDPI reader asks for a larger `w` and gets a
        // proportionally higher-DPI render (240 for w≥1800, 200 for ≥1200, 150 for ≥500), a 1×-display reader (w≈1000) →
        // 150 DPI, rail tiles → 100. maxEdgePixels is raised so the high-DPI A4 render isn't clamped back before the
        // downscale to `w` (the PDF-bomb guard still holds via the DPI cap).
        var dpi = w >= 1800 ? 240 : w >= 1200 ? 200 : w >= 500 ? 150 : 100;
        var render = rot != 0 ? Adelaide.Ai.PdfRotation.WithRotations(bytes, new Dictionary<int, int> { [page] = rot }) : bytes;
        using var bmp = Adelaide.Ai.PdfRenderer.RenderPage(render, page, dpi: dpi, maxEdgePixels: 3000);
        // WebP, never JPEG: these are text-on-white scans; JPEG rings around glyphs. Reading images are lossless (text
        // pixel-exact — part of the record); rail tiles are small lossy.
        var kind = w >= 500 ? Adelaide.Ai.PdfRenderer.PageImageKind.Reading : Adelaide.Ai.PdfRenderer.PageImageKind.Thumbnail;
        var img = Adelaide.Ai.PdfRenderer.Webp(bmp, w, kind);
        cache.Set(imgKey, img, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5), Size = 1 });
        return Results.File(img, "image/webp");
    }
    catch (Exception ex) { app.Logger.LogWarning("Page render failed for {DocId} page {Page} ({ExType}).", id, page, ex.GetType().Name); return Results.NotFound(); }
});

// The real PDF PAGE COUNT for the viewer (how many page images to render) — same inbox-or-filed source (cached).
app.MapGet("/api/pagecount", async (string id, IAuditStore store, IFiledDocumentStore sink, SharePointOptions spOpt, IMemoryCache cache, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest();
    var bytes = await ResolvePdfBytesAsync(id, inbox, store, sink, spOpt, cache, app.Logger, ct);
    if (bytes is null) return Results.NotFound();
    try { return Results.Json(new { pages = Adelaide.Ai.PdfPageOps.PageCount(bytes) }); }
    catch { return Results.Json(new { pages = 1 }); }
});

// Recent documents for the tracking dashboard — straight from the audit projection (survives refresh & restart).
app.MapGet("/api/documents", async (IAuditStore store, string? scope, CancellationToken ct) =>
{
    // active (default) = actionable + in-flight; all = recent of every state; duplicates = open possible-dups (both dedup
    // layers, across states); any other value = that EXACT state (so the count chips can filter to e.g. Filed).
    var docs = scope switch
    {
        "all" => await store.ListDocumentsAsync(5000, ct),
        "duplicates" => await store.ListDuplicatesAsync(5000, ct),
        null or "" or "active" => await store.ListActiveAsync(300, ct),
        _ => await store.ListByStateAsync(scope, 5000, ct),
    };
    // Undiscard affordance (discard-recovery): computed HERE with the same UndiscardGuard the endpoint runs, so
    // the button can never show for a document the POST would refuse (window over, artifacts gone, no restorable
    // predecessor). Discarded rows only — each costs an event-stream read plus two stats, and the population is
    // deliberately tiny (discards seal after the window). The wall clock is read here, in the API layer, like the
    // endpoint's own window check — never in a reducer.
    var undiscardable = new HashSet<string>(StringComparer.Ordinal);
    foreach (var d in docs)
    {
        if (d.State != "Discarded") continue;
        var decision = UndiscardGuard.Evaluate(await store.GetEventsAsync(d.DocumentId, ct), DateTimeOffset.UtcNow, DiscardPurgeGrace.Days,
            pdfExists: File.Exists(Path.GetFullPath(Path.Combine(inbox, $"{d.DocumentId}.pdf"))),
            sidecarExists: File.Exists(Path.GetFullPath(Path.Combine(inbox, $"{d.DocumentId}.review.json"))));
        if (decision.Outcome == UndiscardGuard.Result.Ok) undiscardable.Add(d.DocumentId);
    }
    return Results.Json(docs.Select(d =>
    {
        // Orientation for the preview quick-look: the client passes an EXPLICIT rot to /api/page, so the doc's saved angle
        // is surfaced on the row it already loaded (no extra fetch). Skip the filesystem probe for settled rows (the
        // terminal set + DeadLetter), so a large filed list costs no per-row stat on the Azure Files inbox. `orientable`
        // approximates the PUT's own refusals — the PUT 409s on DocumentStates.Settled and 404s on a missing captured
        // PDF; this list additionally treats DeadLetter as non-orientable (a parked doc's orientation is set after a
        // retry, not while it waits in triage). Under purge-OFF a terminal doc may still HAVE its PDF, which is exactly
        // why the PUT now refuses by STATE rather than trusting file absence (B-4).
        var orientable = false; var savedRotation = 0;
        if (!DocumentStates.IsSettled(d.State) && d.State != "DeadLetter")
        {
            var pdf = Path.Combine(inbox, $"{d.DocumentId}.pdf");
            orientable = File.Exists(pdf);
            if (orientable) savedRotation = UploadProcessor.ReadSavedRotationAngle(pdf);
        }
        return new
        {
            documentId = d.DocumentId, original = d.OriginalName, state = d.State,
            contentType = d.ContentType, disposition = d.Disposition, reasons = d.Reasons,
            reasonCodes = ParseReasonCodes(d.ReasonCodes), confidence = d.Confidence,
            correctedFields = ParseReasonCodes(d.CorrectedFields), // Tier A: names-only; null = not reviewed, [] = reviewed no change
            contentTypeChange = ParseObj(d.ContentTypeChange),     // Tier A: {from,to} type-correction (non-PHI); null unless the type was corrected
            duplicateOf = d.DuplicateOf, lastUpdated = d.LastUpdated,
            savedRotation, orientable, // preview quick-look: current angle + whether its rotate control is enabled
            undiscardable = undiscardable.Contains(d.DocumentId), // Discarded rows only: the POST would succeed right now
        };
    }));
});

// Document counts grouped by state — the dashboard summary, so totals of everything are visible at a glance even when
// the listed rows are bounded (survives a bulk load that far exceeds any list limit). `capture` is the capture-health
// block (docs/CAPTURE.md): rows in the stores vs what the ledger says was processed, review-lane labels expected vs
// landed (AUTOFILED counts separately, never as a failure), and the in-process failure counters. Best-effort — a
// broken capture store degrades this block, never the dashboard counts.
app.MapGet("/api/stats", async (IAuditStore store, IServiceProvider svc, CancellationToken ct) =>
{
    var byState = await store.CountsByStateAsync(ct);
    object capture;
    try
    {
        capture = await CaptureStats.CollectAsync(svc.GetService<Adelaide.Audit.ITranscriptStore>(), store,
            svc.GetService<CaptureHealth>(), ct);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Capture stats unavailable ({ExType}); /api/stats degrades to the document counts.", ex.GetType().Name);
        capture = new { error = "unavailable" };
    }
    return Results.Json(new { total = byState.Values.Sum(), byState, duplicates = await store.CountDuplicatesAsync(ct), capture,
        drainPoller = svc.GetRequiredService<DrainPollerHealth>().Snapshot(),
        // The ingest malware-scanning posture, queryable rather than one INFO line per restart. Disabled with no host
        // is an OPEN GAP with a control pending (migration-plan §7) — NOT an accepted posture, which is what this
        // comment used to claim while citing a §7 row that did not exist. Content validation (size + PDF magic bytes)
        // runs regardless and nothing executes file content, but reviewers upload fax PDFs through the UI and those
        // bytes reach the native renderer. Watch this field: Configured=false in production means unscanned intake.
        malwareScan = MalwareScanning.Posture(),
        // The sweep heartbeat: a stalled `lastTickUtc` is the only externally visible sign that outbox recovery has
        // stopped. Read it against `intervalMinutes` — overdue means the timer is gone, not that there is no work.
        sweep = svc.GetRequiredService<SweepHealth>().Snapshot(),
        // Documents the QUEUE owes a move and has not moved. The pair to `sweep`: that one says the recovery timer is
        // alive, this one says whether it (and the pipeline generally) is actually getting documents out the door —
        // a sweep can tick happily forever while a document sits in Transcribed, because the periodic pass is
        // filingOnly. Expected steady state is 0; see StalledDocuments for what a 0 does NOT prove.
        stalled = await StallGauge.SnapshotAsync(store, DateTimeOffset.UtcNow, ct) });
});

// Operational integrity check: recompute the audit hash chain and report intact + the first broken Seq + the head
// {seq,count,hash} (the anchor triple). Returns NO PHI — a hash and counts. A pass proves internal consistency, NOT
// that the chain is untampered (that needs an external anchor; see AUDIT-INTEGRITY-SPEC.md). Admin-only: integrity state
// is an operator concern, not part of the reviewer workflow.
app.MapGet("/api/audit/verify", async (IAuditStore store, CancellationToken ct) =>
{
    var v = await store.VerifyChainAsync(ct);
    return Results.Json(new { intact = v.Intact, eventCount = v.EventCount, headSeq = v.HeadSeq, headHash = v.HeadHash, firstBrokenSeq = v.FirstBrokenSeq });
}).RequireAuthorization("Admin");

// Per-document custody history: this document's own event stream, in order. Answers "what happened to this document?"
// over HTTP — the only surface that inherits the app's network position and auth model. Before this, the answer required
// a privileged human with VPN reach, a private endpoint and Entra SQL admin ON THE MACHINE BEING RETIRED; the runbooks
// that said "someone runs SQL" resolve here instead.
//
// REVIEWER-VISIBLE (fallback policy), deliberately NOT Admin-only: a reviewer who can already open this document and its
// patient record (FINDING 6 — role-based, not per-object) learns nothing new from its timeline. That choice is exactly
// what makes the Details projection LOAD-BEARING rather than defence-in-depth — see DocumentHistory.
//
// NO CHAIN WALKING: one document, one stream. `supersededBy`/`duplicateOf` come back as ids and the caller follows them
// itself, so the cost of a request is bounded by one document's event count and never by chain depth.
//
// NO HASHES AT ALL — neither prevHash nor the event's own hash. Two separate reasons, and the second is the load-bearing one:
//   (1) Seq is GLOBAL (SqliteAuditStore.NextSeq = MAX(Seq)+1 over the whole table), so one document's events are scattered
//       through the global chain — measured on the localhost ledger, a 20-event document spans Seq 192..7720. A per-document
//       prevHash would chain against events the caller never receives, inviting a reader to believe they had verified
//       custody when they had checked nothing.
//   (2) The event hash is a COMMITMENT TO THE WITHHELD Details. Canon (SqliteAuditStore.Sha256Hex/Canon) is
//       "{prev}|{seq}|{doc}|{unit}|{type}|{at}|{actor}|{corr}|{details}" — and every component except `details` is
//       returned right here. Because Seq is global AND contiguous, prev(N) is simply hash(N-1), so crawling this endpoint
//       across documents reconstructs the whole (seq → hash) map and thus every prevHash. A caller could then take a Filed
//       event whose `redacted` says ["fileName"], guess the filename (its format is known and the only unknown is a KVNR
//       from a list of a few thousand), hash each candidate and confirm the exact value — recovering by brute force
//       precisely what the allowlist withheld, with `redacted` marking which events are worth attacking. Returning the
//       hash would therefore undo the projection. Event identity is (documentId, seq); whole-chain verification stays at
//       /api/audit/verify, and `chain.note` says so in the payload itself.
app.MapGet("/api/documents/{id}/history", async (string id, IAuditStore store, CancellationToken ct) =>
{
    // TIGHTER than the shared IsValidDocId on purpose. That regex accepts any casing and any length, which diverges by
    // substrate: Azure SQL's default CI collation would answer under a mis-cased id (SQLite's BINARY would 404), and an
    // over-long id makes AddWithValue infer nvarchar(max), losing the ix_events_doc seek and full-scanning the append-only
    // events table. The real id shape is Guid("N")[..12] plus an optional split-unit suffix — verified against the live
    // ledger: all 1,601 ids match, max length 15.
    if (!IsLedgerDocId(id)) return Results.BadRequest();
    var events = await store.GetEventsAsync(id, ct);
    if (events.Count == 0)
    {
        // An event-less id is NOT necessarily an unknown one. The C9 migration seeded projection rows for the
        // historical corpus without importing their chains, so those documents appear on the dashboard and have no
        // events of their own. 404 would assert "no such document", which is false and sends the reader back to
        // direct SQL — the workflow this endpoint exists to retire. Report the document with an empty stream and say
        // WHY it is empty; reserve 404 for an id absent from the projection too.
        if (await store.GetDocumentAsync(id, ct) is not { } seeded) return Results.NotFound();
        return Results.Json(new
        {
            documentId = seeded.DocumentId,
            eventCount = 0,
            events = Array.Empty<object>(),
            chain = new
            {
                note = "This document has no events in this ledger. It exists in the document projection only — "
                     + "typically a row seeded by the migration without importing its original chain. An empty stream "
                     + "here means the history was never in this ledger, NOT that nothing happened to the document.",
            },
        });
    }
    return Results.Json(new
    {
        // The LEDGER's own id, never the caller's spelling, so the envelope can never disagree with the rows it wraps.
        documentId = events[0].DocumentId,
        eventCount = events.Count,
        // Materialized EAGERLY: a lazy Select projects during serialization, i.e. after the 200 and the first bytes are
        // already committed, so any throw mid-stream becomes a truncated HTTP 200 carrying an incomplete custody timeline
        // that still parses as a success. Project everything first; fail as a clean 500 if it fails at all.
        events = events.Select(e =>
        {
            var (details, redacted) = DocumentHistory.Project(e.Details);
            return new
            {
                seq = e.Seq, at = e.At, type = e.Type, actor = e.Actor,
                unitId = e.UnitId, correlationId = e.CorrelationId,
                details,            // allowlisted keys only
                redacted,           // names of withheld keys, never their values
            };
        }).ToList(),
        chain = new
        {
            note = "Seq is global across all documents, so this per-document slice is not independently chain-verifiable. Verify the whole chain at /api/audit/verify.",
        },
    });
}).RequireRateLimiting("read");

// Serve a document's PDF. Hex doc-id only — no path traversal. STATE-BASED resolution: the intake copy when present
// (in-flight/review docs), else the FILED doc's PDF STREAMED THROUGH from SharePoint as chunks arrive (no full
// buffer-then-send) with Graph's Content-Length forwarded so the client's progress bar is determinate. Cache-Control:
// no-store on every branch — patient PDFs must never persist in the browser's HTTP disk cache (the client's blob LRU
// is session-bounded memory by design; the disk cache is not). Clean 404 on confirmed not-found. No server-side
// caching: the client blob cache covers repeats, and holding patient PDFs in server memory adds risk for no gain.
app.MapGet("/api/file", async (string id, string? rotate, HttpContext http, IAuditStore store, IFiledDocumentStore sink, SharePointOptions spOpt, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest();
    http.Response.Headers.CacheControl = "no-store";
    var path = Path.GetFullPath(Path.Combine(inbox, $"{id}.pdf")); // absolute — Results.File rejects relative paths
    if (File.Exists(path))
    {
        // Preview-only rotation: serve a /Rotate-APPLIED copy so the browser's native viewer rotates the DOCUMENT (its
        // toolbar unaffected) and the reviewer sees exactly what will be filed — PER PAGE, so a mixed rotation renders
        // correctly. `rotate` is a "page:deg" spec (e.g. "1:270,2:90"). Does NOT alter the stored intake file.
        // Best-effort — any parse failure serves the original.
        var map = ParseRotateSpec(rotate);
        if (map.Count > 0)
        {
            try
            {
                var src = await File.ReadAllBytesAsync(path, ct);
                return Results.File(Adelaide.Ai.PdfRotation.WithRotations(src, map), "application/pdf");
            }
            catch (Exception ex) { app.Logger.LogWarning("Preview rotate failed for {DocId} ({ExType}); serving unrotated.", id, ex.GetType().Name); }
        }
        return Results.File(path, "application/pdf", enableRangeProcessing: true); // inline (no download name) so it renders in the in-page viewer; streams from disk with Content-Length
    }
    var doc = await store.GetDocumentAsync(id, ct);
    if (doc is null) return Results.NotFound();
    var locator = FiledLocator.Require(doc, "api/file", app.Logger);
    if (locator is null) return Results.NotFound(); // tripwire logged; viewer shows the deleted-original placeholder
    using var resp = await sink.OpenFiledPdfStreamAsync(locator, await UploadProcessor.RecordedFiledTargetAsync(store, spOpt, id, ct), ct); // stream from the drive the locator was recorded in (C-2)
    if (resp is null) return Results.NotFound();
    http.Response.ContentType = "application/pdf";
    if (resp.Content.Headers.ContentLength is { } len) http.Response.ContentLength = len; // determinate client progress
    await using (var body = await resp.Content.ReadAsStreamAsync(ct))
        await body.CopyToAsync(http.Response.Body, ct); // chunk-through: bytes flow to the client as Graph delivers them
    return Results.Empty;
});

// Retry a failed document: re-enqueue the captured PDF through the full pipeline. The audit keeps the complete
// history (the DeadLetter stays in the event stream; a new Received event starts the fresh attempt).
var retryGate = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(); // one retry at a time per doc (TOCTOU guard across tabs/users)
app.MapPost("/api/retry", async (string id, HttpContext http, IAuditLog audit, Channel<UploadJob> queue, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest();
    if (!retryGate.TryAdd(id, 0)) return Results.Conflict("Neustart läuft bereits.");
    try
    {
        var doc = await audit.GetDocumentAsync(id, ct);
        if (doc is null) return Results.NotFound();
        if (doc.State != "DeadLetter") return Results.BadRequest("Nur fehlgeschlagene Dokumente können neu gestartet werden.");
        var path = Path.GetFullPath(Path.Combine(inbox, $"{id}.pdf"));
        if (!File.Exists(path)) return Results.NotFound("Original-PDF liegt nicht mehr vor — bitte erneut hochladen.");
        try { File.Delete(Path.GetFullPath(Path.Combine(inbox, $"{id}.review.json"))); } catch (Exception delEx) { app.Logger.LogWarning("Best-effort delete of review sidecar failed for {DocId} (retry; {Error}); stale sidecar remains on disk.", id, delEx.GetType().Name); }
        UploadProcessor.DeleteFilingSidecar(path); // drop any stale filing sidecar so reconcile re-processes, never re-files an old spec
        // From here the pair must complete even if the browser aborts: a Received event without an enqueued job
        // would strand the document (non-terminal, not retryable) — so no request cancellation token.
        await audit.AppendAsync(new AuditEntry(id, "Received", ActorOf(http), Details: J(new { channel = "Retry" }),
            Projection: new ProjectionOverrides(Reasons: "")), CancellationToken.None);
        await queue.Writer.WriteAsync(new UploadJob(id, path, doc.OriginalName ?? $"{id}.pdf", Origin: JobOrigin.Retry), CancellationToken.None);
        return Results.Json(new { ok = true });
    }
    finally { retryGate.TryRemove(id, out _); }
}).RequireCsrfHeader().RequireRateLimiting("expensive");

// Reclassify a FILED document (e.g. auto-filed under the wrong type): reconciles LIVE with SharePoint — removes
// the previously-filed item if it still exists (an item already deleted by hand is fine and is recorded as such),
// then re-runs the full pipeline with FORCED review so a human confirms type + metadata before it is re-filed.
app.MapPost("/api/reprocess", async (string id, HttpContext http, IAuditLog audit, IFiledDocumentStore sink, SharePointOptions spOpt, Channel<UploadJob> queue, FilingClaims claims, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest();
    if (!retryGate.TryAdd(id, 0)) return Results.Conflict("Vorgang läuft bereits.");
    try
    {
        bool removed;
        string path;
        string? originalName, oldContentType;
        var actor = ActorOf(http); // resolved once: both halves of the write-ahead pair are the same human action
        // Hold the SAME per-document claim the filing paths and the C5 aged purge sweep use (verification finding):
        // without it, the sweep could verify-then-delete the inbox copy while this endpoint deletes the SharePoint
        // item — destroying both copies of the document. Scope (second verification round): the claim spans ONLY
        // the dangerous window — rehydrate → WRITE-AHEAD Received → SharePoint delete → sidecar drop — and is
        // RELEASED before the outcome append + enqueue below. Held across the enqueue, an idle worker could pick the job up instantly, find the claim
        // still taken, and DROP it permanently (worker claim contention is a skip, not a requeue) — stranding the
        // doc until a restart whose redrive loses ForceReview. After the delete the sweep cannot touch the doc
        // anyway: its verify of the removed driveItemId fails (keep), and the Received append bumps LastUpdated
        // past any cutoff. retryGate stays for the cross-tab "already running" UX.
        using (var docClaim = claims.Acquire("doc:" + id))
        {
            if (!docClaim.Acquired) return Results.Conflict("Dokument wird gerade verarbeitet oder bereinigt — bitte kurz warten.");
            var doc = await audit.GetDocumentAsync(id, ct);
            if (doc is null) return Results.NotFound();
            if (doc.State != "Filed") return Results.BadRequest("Nur abgelegte Dokumente können korrigiert werden.");
            var locator = FiledLocator.Require(doc, "reclassify", app.Logger);
            if (locator is null) return Results.Problem("Interner Fehler: abgelegtes Dokument ohne SharePoint-Locator — Korrektur nicht möglich.", statusCode: 500); // tripwire
            // The drive the locator was RECORDED in (C-2): resolved once here, then used for the rehydrate, carried
            // on the intent for the delete (inline and every re-drive). A business-filed doc's item lives in the
            // business library; addressing it through the medical default made the rehydrate 404 and the delete
            // "succeed" against nothing.
            var recordedTarget = await UploadProcessor.RecordedFiledTargetAsync(audit, spOpt, id, ct);
            originalName = doc.OriginalName;
            oldContentType = doc.ContentType;
            path = Path.GetFullPath(Path.Combine(inbox, $"{id}.pdf"));
            if (!File.Exists(path))
            {
                // delete-on-verified removed the local copy after filing — rehydrate from SharePoint (the system of record) so
                // reclassify re-runs without a manual re-upload. MUST happen before the SharePoint item is deleted just below.
                var rehydrated = await sink.DownloadFiledPdfAsync(locator, recordedTarget, ct); // by id ONLY (Stage III)
                if (rehydrated is null) return Results.NotFound("Original-PDF liegt nicht mehr vor — bitte erneut hochladen.");
                await File.WriteAllBytesAsync(path, rehydrated, ct);
            }

            // WRITE-AHEAD — the document must leave Filed BEFORE its SharePoint item is destroyed. Previously the
            // Received event was appended only AFTER the delete, so anything failing in between (a ledger write error,
            // a crash) left the chain asserting Filed against a driveItemId that no longer existed. Nothing would ever
            // notice: reconcile skips terminal states, so the document was silently lost — present on the dashboard,
            // absent from SharePoint, with no path back. Recording the intent FIRST inverts that: every failure after
            // this point leaves a NON-terminal document that reconcile owns and re-drives.
            //
            // The cost, accepted deliberately: if the delete below fails, the document is already mid-reclassify and
            // will be re-driven and re-filed while the old item is still live — a possible duplicate, which is visible
            // and handled by the dedup layer. Strictly better than an invisible, unrecoverable loss.
            await audit.AppendAsync(new AuditEntry(id, "Received", actor, Details: J(new
            {
                channel = "Reclassify",
                oldContentType, driveItemIdPendingRemoval = locator,
            }), Projection: new ProjectionOverrides(Reasons: "")), CancellationToken.None);

            // OUTBOX INTENT — written AFTER the write-ahead event, never before (see WriteReclassifySidecarAsync).
            // This is what makes the delete survive a failure: without it, a Graph error left the old item live while
            // the document re-filed, producing a duplicate. With it, reconcile re-drives the delete until it lands.
            var reclassifyIntent = new UploadProcessor.ReclassifyIntent(id, locator, actor, recordedTarget?.SiteId, recordedTarget?.Library);
            await UploadProcessor.WriteReclassifySidecarAsync(path, reclassifyIntent, ct);

            // Inline attempt. A failure here is SWALLOWED, exactly as the supersede half does: the intent is on disk,
            // so reconcile finishes the removal — the request has not failed, the removal is merely pending. Returning
            // 502 would tell the operator the reclassify did not happen when it is in fact under way.
            try
            {
                removed = await sink.TryDeleteByIdAsync(locator, recordedTarget, ct); // id-only (name-keyed path retired); 404 folds to success; drive = the recorded destination
                UploadProcessor.DeleteReclassifySidecar(path);
            }
            catch (Exception ex)
            {
                removed = false;
                app.Logger.LogWarning("Reclassify for {DocId}: SharePoint delete did not complete inline; intent sidecar left for reconcile. Previous item {DriveItemId} is still live until it re-drives. {ExType}", id, locator, ex.GetType().Name);
            }
            // Drop any stale filing sidecar from the PRIOR filing (a best-effort delete that didn't land): moving this doc back
            // to "Received" makes it reconcilable, and a leftover sidecar would make reconcile RE-FILE the old (wrong-type)
            // spec to the path we just cleared — silently reverting the reclassification. No sidecar → reconcile re-enqueues.
            UploadProcessor.DeleteFilingSidecar(path);
        } // claim released — the worker may now take the doc the moment the job lands

        // From here the pair must complete even if the browser aborts (see /api/retry).
        // C6: original and the previous canonical fileName (both patient-identifying) leave the payload; the
        // structural facts of the reclassification (old type, whether the SharePoint item was removed) stay.
        //
        // The OUTCOME half of the write-ahead pair. Deliberately the SAME event type as the intent half: the
        // projection's state is the latest event's Type, so a second "Received" records the removal outcome without
        // moving the document to some other state. It also does not disturb the redrive bound — RedriveWindow counts
        // backwards and stops at the first Received whose actor is not "reconcile", so a second human-actor Received
        // is a stopping point, never a counted redrive. Losing THIS append is now harmless: the document is already
        // non-terminal and reconcile will re-drive it; only the bookkeeping of whether the item was removed is lost.
        await audit.AppendAsync(new AuditEntry(id, "Received", actor, Details: J(new
        {
            channel = "Reclassify",
            oldContentType, sharePointItemRemoved = removed,
        }), Projection: new ProjectionOverrides(Reasons: "")), CancellationToken.None);
        await queue.Writer.WriteAsync(new UploadJob(id, path, originalName ?? $"{id}.pdf", ForceReview: true, Origin: JobOrigin.Reclassify), CancellationToken.None);
        // removalPending: the delete did not land inline and the outbox intent is on disk for reconcile. The
        // reclassify itself HAS happened either way — the document is out of Filed and re-driving.
        return Results.Json(new { ok = true, sharePointItemRemoved = removed, removalPending = !removed });
    }
    finally { retryGate.TryRemove(id, out _); }
}).RequireAuthorization("Admin").RequireCsrfHeader().RequireRateLimiting("expensive"); // destructive: deletes the live SharePoint item and re-files — operator action

// C3e — the invocable erasure surface for the CAPTURE stores (admin-only). scope=content ⇒ DeleteAsync (the
// document's text and model-call bodies go; the measurement rows survive); scope=full ⇒ ForgetAsync (measurement
// too). The act lands in the store's own measure-side erasure log — deliberately NOT a ledger event: the
// projection derives State from the event Type, so a novel event type would corrupt terminal-state semantics and
// re-open the document to reconcile. The ledger's transcriptSha256 stays as proof of what existed. This endpoint
// erases CAPTURE rows only; inbox files and SharePoint artifacts are separate surfaces with their own paths.
app.MapPost("/api/capture/erase", async (string id, string scope, HttpContext http,
    Adelaide.Audit.ITranscriptStore captureStore, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest();
    if (scope is not ("content" or "full")) return Results.BadRequest("scope muss 'content' oder 'full' sein.");
    var result = await CaptureErasure.ExecuteAsync(captureStore, id, scope, ActorOf(http), app.Logger, ct);
    return Results.Json(new { ok = true, removed = result.Removed, logged = result.Logged });
}).RequireAuthorization("Admin").RequireCsrfHeader().RequireRateLimiting("expensive"); // destructive: removes captured PHI rows — operator action

// Patient picker for the review screen — KVNR exact or surname prefix. Returns PHI (names/DOB) by necessity;
// this screen is for authorised reviewers and must sit behind Entra SSO before production.
app.MapGet("/api/patients", async (string? q, IPatientSearch dir, CancellationToken ct) =>
{
    var hits = await dir.SearchAsync(q ?? "", 25, ct);
    return Results.Json(hits.Select(p => new
    {
        listItemId = p.ListItemId, kvnr = p.Kvnr, lastName = p.LastName, firstName = p.FirstName,
        birthDate = p.BirthDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), status = p.Status,
        patientNr = p.PatientNr, klientId = p.KlientId, // PatientNr disambiguates same-name patients; KlientID is the stable cross-system key
    }));
});

// Pre-fill payload for a flagged document: the model's extraction + match candidate + gate reasons, plus the
// catalog field schema for its content type so the review form renders the right inputs (dropdowns, dates…).
app.MapGet("/api/review", async (string? id, IAuditLog audit, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest();
    // Settled documents are not reviewable — STATE FIRST, before the sidecar check, the same order the
    // rotation PUT adopted (B-4: the state outranks the file). Under the recovery regime a settled document's
    // sidecar routinely SURVIVES (discard no longer deletes it; purge-off keeps every filed one), so
    // "no sidecar → 404" stopped being the guard it accidentally was: a stale tab must get the per-state 409,
    // not an editable pre-fill whose submit would 409 anyway.
    var settledState = (await audit.GetDocumentAsync(id, ct))?.State;
    if (DocumentStates.IsSettled(settledState)) return Results.Conflict(TerminalConflictText(settledState!));
    var path = Path.GetFullPath(Path.Combine(inbox, $"{id}.review.json"));
    if (!File.Exists(path)) return Results.NotFound();
    var payload = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, ct));
    (payload as System.Text.Json.Nodes.JsonObject)?.Remove("markdown"); // transcript is server-only — the client never reads it
    // Enrich the duplicate reference with the TARGET's LIVE state + locator so the review overlay can offer "Vorhandenes
    // ersetzen" exactly when it's actionable (target still Filed AND id-bearing), and show the disabled backfill hint for a
    // legacy no-id target. Live lookup (not the stale sidecar) — the target may have been filed/discarded/superseded since
    // this doc was flagged. (Until Stage II Phase 2a backfills ids, most filed targets are legacy → hint, not active.)
    if (payload is System.Text.Json.Nodes.JsonObject po && po["duplicateOf"] is System.Text.Json.Nodes.JsonObject dup
        && dup["documentId"]?.GetValue<string>() is { } targetId)
    {
        var target = await audit.GetDocumentAsync(targetId, ct);
        dup["state"] = target?.State;
        dup["hasLocator"] = !string.IsNullOrEmpty(target?.DriveItemId);
        // The target's saved orientation so the dup-overlay's target pane renders it upright (client passes explicit rot).
        // A filed target has no inbox PDF → 0, which is correct: its /Rotate is already baked into the filed artifact.
        dup["savedRotation"] = UploadProcessor.ReadSavedRotationAngle(Path.GetFullPath(Path.Combine(inbox, $"{targetId}.pdf")));
    }
    // Real PDF page count for the split rail: the sidecar's `pageCount` is the TRANSCRIPT count, which caps at 15 for
    // cost — but a 20–30-page fax stack must show every page to place cuts, and the split ranges must cover the whole
    // document. Best-effort (read-only, no state change); absent → the client falls back to the transcript pageCount.
    if (payload is System.Text.Json.Nodes.JsonObject fpo)
    {
        var pdfPath = Path.GetFullPath(Path.Combine(inbox, $"{id}.pdf"));
        // Persisted orientation (0 when none): the client seeds its optimistic angle from this, so re-opening the queue
        // shows the reviewer's earlier rotation instead of resetting to upright. Read-only, never throws.
        fpo["savedRotation"] = UploadProcessor.ReadSavedRotationAngle(pdfPath);
        try
        {
            if (File.Exists(pdfPath)) fpo["filePageCount"] = Adelaide.Ai.PdfPageOps.PageCount(await File.ReadAllBytesAsync(pdfPath, ct));
        }
        catch { /* unreadable → omit; the client uses the transcript pageCount */ }
    }
    // The content type is REVIEWER-EDITABLE in the form, so the field schema is delivered PER TYPE: the client renders
    // the fields of the SELECTED type (incl. its required fields, e.g. Verordnung → Verordnungsfolge), not just the
    // document's original classification. Without this the reviewer could switch a doc to Verordnung, never see the
    // required Verordnungsfolge field, and hit the server's required-field rejection with no way to fill it. `schema`
    // (the original type's) stays for back-compat. Vocab.For is a static dictionary lookup, so building all types is cheap.
    static object[] SchemaOf(DocTypeDef? d) => (d?.Fields ?? Array.Empty<DocField>())
        .Where(f => f.Kind != FieldKind.Lookup)
        .Select(f => (object)new { column = f.Column, label = f.Label, kind = (f.Kind is FieldKind.Date or FieldKind.Bool or FieldKind.Choice ? f.Kind.ToString() : "Text"), required = f.Required, requiredUnless = f.RequiredUnless is { } ru ? new { field = ru.Field, values = ru.Values } : null, allowed = Vocab.For(f.Column) })
        .ToArray();
    var contentType = payload?["contentType"]?.GetValue<string>() ?? DocumentCatalog.Fallback;
    var schemas = DocumentCatalog.Types.ToDictionary(t => t.ContentType, t => SchemaOf(t));
    var schema = SchemaOf(DocumentCatalog.Find(contentType)); // original type's schema — back-compat fallback for the client
    return Results.Json(new { payload, schema, schemas, types = DocumentCatalog.Types.Select(t => t.ContentType) });
});

// Submit a human review: file the reviewer-confirmed document to SharePoint (complete metadata + searchable PDF +
// Volltext) and record the model-vs-human corrections as a tamper-evident audit event (the accuracy / feedback log).
app.MapPost("/api/review", async (ReviewSubmission body, HttpContext http, IFiledDocumentStore sink, SharePointOptions spOpt, IAuditLog audit, FilingClaims claims, IServiceProvider sp, CancellationToken ct) =>
{
    var id = body.DocumentId;
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest("invalid documentId");
    // Drop stray model/sidecar provenance keys (a pre-fix hallucinated column, a stale key) BEFORE validating: the
    // reviewer cannot see or remove them, so rejecting would brick a correct review — pruning keeps the ledger just
    // as clean. Only the count is logged, never the keys (a hallucinated "column" can be arbitrary text).
    body = ReviewGuard.Prune(body, out var droppedKeys);
    if (droppedKeys > 0) app.Logger.LogWarning("Review submit for {DocId}: dropped {Count} non-vocabulary field key(s) (model/sidecar provenance, not reviewer input) before recording.", id, droppedKeys);
    // Conditional patient guard (pure core, unit-tested): a MEDICAL submission must name a patient; a BUSINESS one must
    // not — and cannot supersede. "Business" is the explicit body.IsBusiness flag, never inferred from an empty patient.
    switch (ReviewGuard.Validate(body))
    {
        case ReviewGuard.Result.ContentTypeRequired: return Results.BadRequest("contentType required");
        case ReviewGuard.Result.PatientRequired: return Results.BadRequest("a patient must be selected");
        case ReviewGuard.Result.BusinessCannotSupersede: return Results.BadRequest("Ein Geschäftsdokument kann kein vorhandenes Dokument ersetzen.");
        case ReviewGuard.Result.ZielbereichRequired: return Results.BadRequest("Bitte einen Zielbereich wählen (HR / Finance / unklar).");
        case ReviewGuard.Result.UnknownContentType: return Results.BadRequest("Unbekannter Dokumenttyp — bitte einen Typ aus dem Katalog wählen.");
        case ReviewGuard.Result.InvalidFieldName: return Results.BadRequest("Ungültiger Feldname in der Übermittlung.");
    }

    // Mandatory fields must be present (defence in depth: the UI blocks submit and the gate routes blanks to review,
    // but a direct API call or a UI regression must not slip a required SharePoint column through empty). MEDICAL only —
    // a business filing writes minimal holding-library metadata and has no catalog required-fields.
    var typeDef = body.IsBusiness ? null : DocumentCatalog.Find(body.ContentType);
    if (typeDef is not null)
    {
        string? Val(string c) => body.Fields is not null && body.Fields.TryGetValue(c, out var x) ? x : null;
        var missing = typeDef.Fields
            .Where(f => f.IsRequiredGiven(Val) && string.IsNullOrWhiteSpace(Val(f.Column)))
            .Select(f => f.Label).ToList();
        if (missing.Count > 0) return Results.BadRequest($"Pflichtfeld(er) fehlen: {string.Join(", ", missing)}");
    }

    var sidecarPath = Path.GetFullPath(Path.Combine(inbox, $"{id}.review.json"));
    var pdfPath = Path.GetFullPath(Path.Combine(inbox, $"{id}.pdf"));
    if (!File.Exists(sidecarPath)) return Results.NotFound("no pending review for this document");

    // Claim the document (non-blocking): a concurrent worker or a second reviewer fails the claim and gets a clean
    // Conflict instead of blocking a request thread for the entire SharePoint upload. The Filed check is then safe.
    using var docClaim = claims.Acquire("doc:" + id);
    if (!docClaim.Acquired) return Results.Conflict("Dokument wird gerade verarbeitet oder abgelegt — bitte kurz warten.");
    // A stale tab must not file a SETTLED document a second time. This tested only own-Filed until the
    // supersede-lifecycle review: wherever the review sidecar survives (the entire purge-OFF posture), a Superseded
    // or Discarded doc could still be re-filed here — appending Reviewed+Filed OVER the terminal event and flipping
    // the projection to a self-consistent-looking Filed. Guard the whole terminal set (DocumentStates).
    var settledState = (await audit.GetDocumentAsync(id, ct))?.State;
    if (DocumentStates.IsSettled(settledState))
        return Results.Conflict(TerminalConflictText(settledState!));

    var model = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(sidecarPath, ct))!;

    // SUPERSEDE — validate AT SUBMIT TIME (compare-time checks go stale: two pending dups of one original can race, or
    // the target could have been discarded/superseded meanwhile). The target MUST be THIS doc's own recorded duplicateOf
    // (never a general delete lever), currently Filed, and id-bearing (the delete is by driveItemId only; a legacy no-id
    // target is UI-disabled with a backfill hint AND rejected here as defence in depth). Any failure → clean German
    // error, NO partial action (nothing has been filed or deleted yet).
    var dupOfObj = model["duplicateOf"] as System.Text.Json.Nodes.JsonObject;
    var dupOfDocId = dupOfObj?["documentId"]?.GetValue<string>();
    DocumentAudit? supersedeTarget = null;
    if (!string.IsNullOrEmpty(body.SupersedeTarget))
    {
        supersedeTarget = await audit.GetDocumentAsync(body.SupersedeTarget, ct);
        switch (SupersedeGuard.Validate(body.SupersedeTarget, dupOfDocId, supersedeTarget)) // pure, unit-tested core
        {
            case SupersedeGuard.Result.MismatchDuplicate: return Results.Conflict("Ersetzen ist nur für das erkannte Duplikat möglich."); // pins the lever to duplicateOf; also rejects a non-business-key source
            case SupersedeGuard.Result.TargetNotFiled: return Results.Conflict("Das zu ersetzende Dokument ist nicht mehr abgelegt — bitte den Vergleich neu laden.");
            case SupersedeGuard.Result.TargetNoLocator: return Results.BadRequest("Das vorhandene Dokument hat keinen SharePoint-Locator (Altbestand) — Ersetzen nicht möglich, Backfill erforderlich.");
        }
    }
    // Claim the PREDECESSOR for the whole supersede (B-2): the submit mutates TWO documents — it files the successor
    // and deletes + terminalises the predecessor — but the claim above covers only the successor's id, so
    // /api/reprocess could acquire doc:{predecessor} uncontended inside this window (which spans the multi-second
    // SharePoint upload plus the rotation bake), revive the document, and the terminal Superseded then landed on a
    // live, mid-pipeline document. Successor-then-predecessor cannot deadlock or mutually 409: acquisition is a
    // non-blocking TryAdd, and the two claim classes are disjoint by construction (a successor is in review, a
    // predecessor is Filed — SupersedeGuard requires it — so no two supersedes can hold each other's keys crosswise).
    // This also retires DELETION-SITES.md row 18's accepted race (1): the C5 sweep claims each doc it purges and
    // skips on contention, so it can no longer release an edge-aged predecessor's inputs inside the supersede's tick.
    using var predecessorClaim = supersedeTarget is not null ? claims.Acquire("doc:" + supersedeTarget.DocumentId) : default;
    if (supersedeTarget is not null && !predecessorClaim.Acquired)
        return Results.Conflict("Das zu ersetzende Dokument wird gerade verarbeitet oder bereinigt — bitte kurz warten.");

    // Guardrail: a document the worker flagged as a possible duplicate may only be filed once the reviewer has
    // EXPLICITLY acknowledged it (the UI forces opening the side-by-side compare first, then a confirm checkbox).
    // Enforced server-side too, so a direct call or a UI regression can't slip a duplicate through silently. SUPERSEDE
    // IMPLIES the acknowledgement (the reviewer just judged both documents on the compare screen) — no stacked prompt.
    var acknowledgedDuplicate = body.AcknowledgedDuplicate || supersedeTarget is not null;
    if (dupOfObj is not null && !acknowledgedDuplicate)
        return Results.Json(new { detail = "Mögliches Duplikat — bitte im Vergleich prüfen und die Ablage ausdrücklich bestätigen.", requiresDuplicateAck = true }, statusCode: 409);

    var original = model["original"]?.GetValue<string>();
    var markdown = model["markdown"]?.GetValue<string>() ?? "";
    var pageCount = model["pageCount"]?.GetValue<int>() ?? 1;

    // The audit actor is the authenticated Entra oid (Easy Auth) — never a client-supplied value, so a caller can't
    // write an arbitrary/launderable "who" into the immutable ledger. Fail-closed: ActorOf throws
    // if there is no principal (unreachable behind the Reviewer policy). It propagates through the filing sidecar →
    // reconcile recovery, so a recovered filing keeps the true reviewer. reviewDiff is computed once (stored in the sidecar
    // for crash recovery AND recorded inline); for a business submission it carries the domainChange (§11) — the labeled seed.
    var reviewer = ActorOf(http);
    // Model-side names refused by the ledger vocabulary — the fallback-type case the extractor's per-type schema check
    // cannot cover. COUNT ONLY, never the names: a hallucinated "column" can be arbitrary text (same rule as the
    // client-side prune above). A non-zero count here means the extractor proposed columns outside the catalog.
    if (ReviewDiff.OffVocabularyModelFields(model, body.IsBusiness) is > 0 and var offVocab)
        app.Logger.LogWarning("Review submit for {DocId}: {Count} model-proposed field name(s) outside the ledger vocabulary were excluded from the recorded diff.", id, offVocab);
    var reviewDiff = ReviewDiff.Compute(model, body);

    // Fork on domain. Both lanes then run the SAME idempotent outbox (sidecar → Filing → FileAsync → Reviewed → Filed),
    // so the crash-safe recovery invariant holds identically; only the spec, the patient, and the event shape differ.
    FilingSpec spec;
    int? filedPatientId;
    object filingDetails;
    if (body.IsBusiness)
    {
        // Business: NO patient, minimal holding-library metadata (DocumentDate only), the configured business content
        // type + destination. The FilingTarget on the spec makes reconcile re-drive to the business library after a crash.
        var documentDate = body.Fields is not null && body.Fields.TryGetValue("DocumentDate", out var dd) ? dd : null;
        var sourceChannel = model["channel"]?.GetValue<string>(); // recorded in the audit (non-PHI); not a SharePoint column in Phase 1
        var transcript = new Transcript(markdown, pageCount, 1.0, Array.Empty<DecodedBarcode>());
        var unit = new DocumentUnit(id, 1, pageCount, transcript);
        var doc = new IncomingDocument(id, pdfPath, SourceChannel.Api, original);
        var target = spOpt.BusinessTarget();
        spec = SharePointSink.BuildBusinessSpec(doc, unit, target, target.ContentType ?? DocumentCatalog.Fallback, documentDate, body.Zielbereich);
        filedPatientId = null; // NEVER a patient lookup for a business filing
        filingDetails = new { contentType = spec.ContentType, domain = "Business", siteId = target.SiteId, library = target.Library, sourceChannel }; // C6: fileName reaches the projection via the side-channel on the append, not the payload; siteId+library = the full recorded destination (review finding: library alone left the site axis to current config, so a later SP_BUSINESS_SITE_ID cutover would re-address history)
    }
    else
    {
        // Reviewer-confirmed extraction → full confidence, provenance "review".
        var fields = new List<ExtractedField> { new("ContentType", body.ContentType, 1.0, "review") };
        foreach (var (k, v) in body.Fields ?? new Dictionary<string, string?>())
            if (!string.IsNullOrWhiteSpace(v) && !string.Equals(k, "ContentType", StringComparison.OrdinalIgnoreCase))
                fields.Add(new ExtractedField(k, v!.Trim(), 1.0, "review"));
        var data = new ExtractedData(fields);

        // EXACT day-first parse (the date-input emits ISO; dotted German defensively). The prior culture-DEFAULT lenient
        // parse was day-first on a German host but month-first in the Azure container — the works-in-dev-swaps-in-prod trap.
        DateOnly? dob = DateOnly.TryParseExact(data.Value("BirthDate"), new[] { "yyyy-MM-dd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var patient = new PatientRef(body.PatientListItemId, data.Value("KVNR") ?? "", data.Value("LastName") ?? "", data.Value("FirstName") ?? "", dob, "Active");
        var match = new PatientMatch(MatchOutcome.Single, patient, "review");
        var transcript = new Transcript(markdown, pageCount, 1.0, Array.Empty<DecodedBarcode>());
        var unit = new DocumentUnit(id, 1, pageCount, transcript);
        var doc = new IncomingDocument(id, pdfPath, SourceChannel.Api, original);
        var result = new ProcessingResult(id, Disposition.AutoFile, Array.Empty<string>(), data, match, 1.0);

        // No business-key dedup block here: a human reviewer is the final authority. Suspected duplicates are flagged to
        // review by the worker (with the side-by-side compare view); blocking the submit would strand the reviewer with no
        // recourse — and the key alone (patient/type/date) wrongly collides distinct same-day Genehmigungen anyway.
        spec = SharePointSink.BuildSpec(doc, unit, result);
        filedPatientId = body.PatientListItemId;
        filingDetails = new { contentType = body.ContentType, patientListItemId = body.PatientListItemId }; // C6: fileName via the side-channel on the append
    }

    // ROTATE (Phase 1): a reviewer-requested per-page /Rotate is an explicit, hash-chained transform applied to the inbox
    // PDF (which FileAsync then files + DI re-OCRs upright) via the crash-safe rotate outbox — BEFORE the filing outbox,
    // so a filed doc can never carry an un-audited rotation. That guarantee lives in the CATCHES below KEEPING the
    // intent: a bake that fails after the byte swap aborts the submit with the .rotate.json still on disk, so either
    // the reconcile rotate pass completes the audit trail or the next submit finishes the pending rotation first —
    // the doc cannot reach the filing step with a swapped-but-unrecorded PDF. (C-1: these catches used to DELETE the
    // intent, leaving changed PHI bytes with no event and nothing for any sweep to find — permanent, because the
    // idempotence guard below then saw the bytes already rotated and never re-recorded.) First FINISH any rotation a
    // prior attempt left in-flight (audit the byte change before we file), then apply this submission's rotation.
    // Fail-safe: a PDF PdfSharp cannot parse leaves the original untouched + returns an error (never a corrupted
    // filed artifact). Idempotent: absolute targets, so a page already at the requested /Rotate is a no-op (no event).
    if (UploadProcessor.ReadRotateSidecar(pdfPath) is { } pendingRotation)
    {
        // KEEP the intent on failure (mirrors the reconcile rotate pass): if the throw came after the byte swap,
        // the sidecar is the only record that an un-audited change exists, and the sweep re-drives it.
        try { await UploadProcessor.DriveRotateAsync(audit, pdfPath, pendingRotation, app.Logger, ct); }
        catch (Exception rex) { return Results.BadRequest($"Eine ausstehende Drehung konnte nicht abgeschlossen werden ({UploadProcessor.SanitizeError(rex)}); sie bleibt vorgemerkt und wird automatisch wiederholt — bitte erneut versuchen."); }
    }
    // Then bake the reviewer's SAVED orientation — the .rotation.json this doc accumulated via PUT
    // /api/documents/{id}/rotation (per rotate click), which the client awaited before this POST (so it is never stale).
    // A single document-wide absolute angle → expand to a per-page /Rotate map over the real page count and apply via the
    // SAME crash-safe outbox. Absolute + idempotent: a page already at the target is a no-op, so an unrotated doc (angle 0)
    // files byte-identically to before — no intent, no "Rotated" event.
    var savedAngle = UploadProcessor.ReadSavedRotationAngle(pdfPath);
    if (savedAngle != 0)
    {
        byte[] preBytes;
        try { preBytes = await File.ReadAllBytesAsync(pdfPath, ct); }
        catch { return Results.NotFound("no pending review for this document"); }
        int filePages;
        IReadOnlyDictionary<int, int> current;
        try { filePages = Adelaide.Ai.PdfPageOps.PageCount(preBytes); current = Adelaide.Ai.PdfRotation.ReadRotations(preBytes); }
        catch { return Results.BadRequest("Die PDF-Datei konnte nicht gelesen werden (beschädigt?) — die Drehung wurde nicht angewendet."); } // fail-safe: malformed → original untouched
        var targets = new Dictionary<int, int>();
        for (var p = 1; p <= filePages; p++) targets[p] = savedAngle; // doc-wide: every page to the same absolute angle
        var needsRotate = targets.Any(kv => !current.TryGetValue(kv.Key, out var c) || c != kv.Value); // skip if the bytes already carry it (idempotent re-submit)
        if (needsRotate)
        {
            var intent = new UploadProcessor.RotateIntent(id, targets, UploadProcessor.Sha256Hex(preBytes), reviewer);
            await UploadProcessor.WriteRotateSidecarAsync(pdfPath, intent, ct); // intent BEFORE the byte change
            // KEEP the intent on failure. The throw can come BEFORE the byte swap (nothing changed — the sweep's
            // re-drive is then a clean retry) or AFTER it (the Rotated append failed — the sidecar is the only
            // record that the bytes changed, and deleting it here was C-1: a permanent, un-audited PHI byte change,
            // because the idempotence guard above never re-records an already-rotated file). The caller cannot tell
            // the two apart, so the message promises neither "unchanged" nor "applied" — only that the rotation
            // stays recorded and is re-driven automatically.
            try { await UploadProcessor.DriveRotateAsync(audit, pdfPath, intent, app.Logger, ct); }
            catch (Exception rex)
            {
                return Results.BadRequest($"Die Seiten konnten nicht gedreht werden ({UploadProcessor.SanitizeError(rex)}); die Drehung bleibt vorgemerkt und wird automatisch wiederholt — bitte erneut versuchen.");
            }
        }
    }

    // Same outbox as the worker path: write the sidecar (recovery payload — incl. the reviewer + correction/domain diff +
    // duplicate-ack AND the destination target, so a filing recovered by reconcile keeps full audit fidelity and lands in
    // the RIGHT library) + the atomic "Filing" fact BEFORE the upload, so a crash here leaves a recoverable "Filing" doc
    // that reconcile completes — not a filed-but-unrecorded one.
    await UploadProcessor.WriteFilingSidecarAsync(pdfPath, spec, filedPatientId, ct, reviewer, reviewDiff, acknowledgedDuplicate);
    await audit.AppendAsync(new AuditEntry(id, "Filing", reviewer, Details: J(filingDetails),
        Projection: new ProjectionOverrides(FileName: spec.FileName)), ct);
    FilingResult filing;
    try { filing = await sink.FileAsync(spec, ct); }
    catch (Exception ex) { return Results.Problem($"Ablage in SharePoint fehlgeschlagen: {UploadProcessor.SanitizeError(ex)} — Wiederholung läuft automatisch.", statusCode: 502); } // sink errors echo Graph bodies (filename can carry KVNR) — sanitize; doc stays "Filing", reconcile completes it
    var driveItemId = filing.DriveItemId;

    await audit.AppendAsync(new AuditEntry(id, "Reviewed", reviewer, Details: J(reviewDiff)), ct);
    // The Filed event: a business filing carries a domain marker (NO patient); a medical one carries the patient + the
    // reciprocal `supersedes: <oldDocId>` when this is a supersede (only then, so the 99% non-supersede filing keeps its
    // exact event shape). The old doc gets the matching `supersededBy` below. C10: every Filed carries whether the
    // uploaded artifact declares PDF/A and whether it is the DI rendition — the durable record-of-file assertion.
    object filedDetails = body.IsBusiness
        // businessDedupKey: the content fingerprint (over the same LeadWith(transcript) markdown the worker fingerprints),
        // recorded so a later re-scan of this document is flagged as a duplicate (Layer-3, worker) → the compare overlay.
        ? new { contentType = spec.ContentType, domain = "Business", siteId = spec.Target?.SiteId, library = spec.Target?.Library, businessDedupKey = BusinessDedup.Fingerprint(markdown), disposition = "ReviewedThenFiled", acknowledgedDuplicate, driveItemId, pdfaDeclared = filing.PdfADeclared, diRendition = filing.DiRendition }
        : supersedeTarget is null
            ? new { contentType = body.ContentType, patientListItemId = body.PatientListItemId, disposition = "ReviewedThenFiled", acknowledgedDuplicate, driveItemId, pdfaDeclared = filing.PdfADeclared, diRendition = filing.DiRendition }
            : new { contentType = body.ContentType, patientListItemId = body.PatientListItemId, disposition = "ReviewedThenFiled", acknowledgedDuplicate, driveItemId, pdfaDeclared = filing.PdfADeclared, diRendition = filing.DiRendition, supersedes = supersedeTarget.DocumentId };
    // N4: filing is the one path that does NOT run IntakePipeline.Decide — the human is the decision. So the shared
    // empty-transcript predicate is applied here too, not to block the reviewer (they may well have read a photo-only
    // document on screen and be filing it deliberately) but so the condition is RECORDED rather than silent. Without
    // this, a document filed with no usable text is indistinguishable in the ledger from one filed with a full
    // transcript: it lands in SharePoint with no Volltext, unsearchable, and nothing says why. Same predicate as the
    // gate, so the two definitions cannot drift apart. See TranscriptQuality / ReasonCodes.EmptyTranscript.
    var unusableTranscript = TranscriptQuality.IsUnusable(markdown, pageCount, null);
    if (unusableTranscript)
        app.Logger.LogWarning(
            "Filed {DocId} with an unusable transcript ({Chars} chars over {Pages} page(s)) — SharePoint Volltext will be empty and the document unsearchable. reasonCode={Code}",
            id, markdown.Trim().Length, pageCount, ReasonCodes.EmptyTranscript);

    // Carried as a PROPERTY of the Filed event rather than as a separate event: NeedsReview/Filed are state-bearing,
    // and emitting one here would fake a state transition the document never made. The normal payload is passed
    // through UNTOUCHED — only the rare unusable case is augmented — so every ordinary Filed event serialises exactly
    // as it did before, which matters because these events are hash-chained.
    object filedPayload = filedDetails;
    if (unusableTranscript)
    {
        var augmented = System.Text.Json.JsonSerializer.SerializeToNode(filedDetails)!.AsObject();
        augmented["reasonCodes"] = new System.Text.Json.Nodes.JsonArray(ReasonCodes.EmptyTranscript);
        filedPayload = augmented;
    }
    await audit.AppendAsync(new AuditEntry(id, "Filed", "sharepoint", Details: J(filedPayload),
        Projection: new ProjectionOverrides(FileName: spec.FileName)), ct); // C6: the canonical name (embeds patient name/KVNR) is projection-only

    // SUPERSEDE, second half — FILE-FIRST (done, just above), THEN DELETE. Record the delete INTENT (outbox sidecar with
    // the full terminal payload) BEFORE the Graph delete, then drive it. The window between the Filed append above and
    // this sidecar write is ACCEPTED: its only failure mode is BOTH docs filed (visible redundancy, human-fixable) —
    // never a lost document. From the sidecar onward everything is recovered idempotently by reconcile's supersede pass;
    // an inline failure here is swallowed (the new doc IS filed — the request succeeds) and reconcile finishes the delete.
    if (supersedeTarget is not null)
    {
        // The predecessor's RECORDED destination rides the intent (C-2), so the delete — inline and on every
        // reconcile re-drive — addresses the drive its locator was recorded in, not the medical default.
        var oldTarget = await UploadProcessor.RecordedFiledTargetAsync(audit, spOpt, supersedeTarget.DocumentId, ct);
        var intent = new UploadProcessor.SupersedeIntent(supersedeTarget.DocumentId, supersedeTarget.DriveItemId!, id, reviewer, "vom Nutzer ersetzt", oldTarget?.SiteId, oldTarget?.Library);
        await UploadProcessor.WriteSupersedeSidecarAsync(pdfPath, intent, ct);
        try { await UploadProcessor.DriveSupersedeAsync(sink, audit, pdfPath, intent, app.Logger, ct); }
        catch (Exception ex) { app.Logger.LogWarning("Supersede for {NewDocId} did not complete inline (old {OldDocId}); intent sidecar left for reconcile. {ExType}", id, supersedeTarget.DocumentId, ex.GetType().Name); }
    }

    // THE LABEL. This is the moment the human's answer exists, and writing it here is what turns a retained document
    // into a SCOREABLE one — `Label IS NOT NULL` is the entire corpus query. Non-PHI (a category and a lane), and a
    // no-op when the document has no corpus row (retention was off when it was transcribed). Best-effort: a metrics
    // write must never fail a filing that has already succeeded in SharePoint.
    if (sp.GetService<Adelaide.Audit.ITranscriptStore>() is { } corpusStore)
    {
        // CHECKED label write: a false return (no corpus row) is warned + counted, never silent — the silent
        // swallow that let 5 straight filings no-op invisibly is exactly what this replaces.
        await ReviewCapture.SetLabelCheckedAsync(corpusStore, sp.GetService<CaptureHealth>(), app.Logger, id,
            body.ContentType, body.IsBusiness ? DocumentDomain.Business : DocumentDomain.Medical, driveItemId, DateTimeOffset.UtcNow, ct);

        // Per-field capture: outcomes onto corpus_fields (FILLs inserted) + the adjudicated value pairs (PHI side) —
        // the review-lane half of the DocumentDate reconstruction test — with the client's SANITIZED last-writer map
        // landing in field_adjudications.Provenance. Fail-open inside; the filing already succeeded.
        await ReviewCapture.RecordReviewOutcomesAsync(corpusStore, sp.GetService<CaptureHealth>(), app.Logger,
            id, model, body, ReviewCapture.SanitizeProvenance(model, body), DateTimeOffset.UtcNow, ct);
    }

    UploadProcessor.DeleteFilingSidecar(pdfPath);   // recovery payload, no transcript — always safe to drop once settled
    // The REVIEW sidecar holds the transcript, so it is evidence: kill-switch-gated (the ungated era cost the
    // transcripts of every August filing — 100% PDF kept, 5% transcript kept) and, since the A4 fold-in, ALSO
    // grace-deferred like the PDF one line below — one repair window, one release at the aged sweep. NB with
    // purging disabled the inbox retains review sidecars for filed documents; that is the same deliberate,
    // temporary trade PurgeGate already documents for the PDFs.
    UploadProcessor.DeleteReviewSidecarIfPurgeEnabled(sidecarPath, id, app.Logger);
    await UploadProcessor.PurgeFiledInputsAsync(sink, id, pdfPath, driveItemId, spec.Target, app.Logger, ct); // delete-on-verified (by id, in the spec's target drive)
    return Results.Json(new { ok = true, fileName = spec.FileName });
}).RequireCsrfHeader();

// SPLIT (manual, in review): the reviewer partitions an in-review document into contiguous page ranges; each range
// becomes a child unit that RE-ENTERS the full pipeline (transcribe→classify→gate→file/review) as a clean single doc.
// The parent's terminal Segmented event is the COMMIT and the tamper-evident file→units link, appended BEFORE the
// children materialise; a .split.json intent (written before the commit) makes the fan-out crash-safe — reconcile's
// split pass re-derives missing children from the recorded ranges and re-enqueues them, on every sweep, not just at
// startup. (Before C-3's fix the commit came LAST with no intent: a failed Segmented append left children recorded as
// units of a split that never committed, the parent still re-filable, and nothing ever re-attempting the commit.)
// The parent is never filed as one — its content now lives in the children.
app.MapPost("/api/split", async (SplitSubmission body, HttpContext http, IAuditLog audit, IAuditStore store, Channel<UploadJob> queue, FilingClaims claims, CancellationToken ct) =>
{
    var id = body.DocumentId;
    if (!IsValidDocId(id)) return Results.BadRequest("invalid documentId");

    // Hold the SAME per-document claim every other writer of this document takes — the review submit, reconcile, the
    // reaper and /api/reprocess. Split was the ONE mutating path that took none, and its terminal guard below is a
    // read-then-act: it reads the parent's state, then (much later) appends the TERMINAL `Segmented` event. A review
    // submit filing the same parent in that window passes the guard, and the parent then lands terminal underneath the
    // in-flight filing — whose crash recovery is reconcile, which excludes terminal states at BOTH of its gates (the
    // list query and, since the D-1 fix, the re-read under the claim; before that fix the re-read tested "Filed" alone
    // and a parent turning Segmented mid-pass was FILED from its sidecar instead of skipped). The filing sidecar is
    // then reaped, never replayed, and an upload that already landed stays unrecorded. The claim closes the CONCURRENT
    // window by making the read-then-act atomic against the other writers; the SEQUENTIAL variant — a parent whose
    // filing a previous request left in doubt — is refused by the entry guard below (A1).
    //
    // Held for the WHOLE handler (unlike /api/reprocess, which must release before its enqueue): the jobs enqueued
    // here are the CHILDREN, whose ids are `{id}-u{n}` and therefore claim different keys — so an idle worker picking
    // one up instantly cannot contend with this claim. It also covers the parent PDF/sidecar deletes at the end
    // against the C5 aged purge sweep.
    //
    // THIS EXCLUSION IS ONLY AS STRONG AS worker_count = 1. FilingClaims is an in-process dictionary, so a second app
    // instance would hold its own claims and see none of these — the guard below would pass on both instances at once
    // and the race would be live again. The count is pinned at infra/azure/main.tf:95; the host mutex does NOT cover
    // this case (it is machine-scoped). See FilingClaims for the full invariant and what else rests on it.
    using var docClaim = claims.Acquire("doc:" + id);
    if (!docClaim.Acquired) return Results.Conflict("Dokument wird gerade verarbeitet oder abgelegt — bitte kurz warten.");

    var sidecarPath = Path.GetFullPath(Path.Combine(inbox, $"{id}.review.json"));
    var pdfPath = Path.GetFullPath(Path.Combine(inbox, $"{id}.pdf"));
    if (!File.Exists(sidecarPath)) return Results.NotFound("no pending review for this document");
    if (!File.Exists(pdfPath)) return Results.NotFound("document PDF not found");
    var parent = await store.GetDocumentAsync(id, ct);
    // Same terminal guard as /api/review (previously Segmented/Filed only): a settled parent must not be split into
    // fresh children over its terminal event — the children would re-enter the pipeline under a document the ledger
    // already closed.
    if (parent is not null && DocumentStates.IsSettled(parent.State))
        return Results.Conflict(TerminalConflictText(parent.State));
    // A1 entry guard: a parent whose FILING IS IN DOUBT must not be split. `Filing` is not terminal — it is what a
    // 502 from the filing step leaves behind, with the `.filing.json` intent still on disk — and reconcile WILL
    // complete that filing from the sidecar. Splitting such a parent lands it `Segmented` underneath the in-doubt
    // filing, and reconcile is then excluded twice (the state arm as terminal, the orphan-sidecar arm as
    // not-NeedsReview): an item already uploaded to SharePoint stays live with no Filed event anywhere, and no sweep
    // ever looks again. Refuse BOTH signals of doubt — the state itself, and a surviving filing intent regardless of
    // the state label (the sidecar's existence, not the label, marks an in-doubt filing; reconcile completes either).
    // The per-document claim above closes only the CONCURRENT window; this closes the SEQUENTIAL one — state a
    // previous request left behind, which no claim can see.
    if (parent is { State: "Filing" } || File.Exists(UploadProcessor.FilingSidecarPath(pdfPath)))
        return Results.Conflict("Für dieses Dokument läuft noch eine Ablage (oder deren automatische Wiederherstellung) — Aufteilen ist erst möglich, wenn sie abgeschlossen ist.");

    var parentBytes = await File.ReadAllBytesAsync(pdfPath, ct);
    int pageCount;
    try { pageCount = Adelaide.Ai.PdfPageOps.PageCount(parentBytes); }
    catch { return Results.BadRequest("Die PDF-Datei konnte nicht gelesen werden (beschädigt?)."); }

    // The reviewer PARTITIONS the doc: ≥2 contiguous ranges covering 1..pageCount with no gaps or overlaps.
    var ranges = (body.Ranges ?? Array.Empty<int[]>()).Where(r => r.Length == 2).Select(r => (First: r[0], Last: r[1])).ToList();
    if (ranges.Count < 2) return Results.BadRequest("Mindestens zwei Teile erforderlich, um aufzuteilen.");
    var expected = 1;
    foreach (var (first, last) in ranges)
    {
        if (first != expected || last < first || last > pageCount) return Results.BadRequest($"Ungültige Seitenbereiche — lückenlos 1–{pageCount} erforderlich.");
        expected = last + 1;
    }
    if (expected != pageCount + 1) return Results.BadRequest($"Die Teile müssen alle {pageCount} Seiten lückenlos abdecken.");

    const string sepVersion = "split-manual-v1";
    var actor = ActorOf(http);
    var fileSha = parent?.Sha256 ?? id;
    var parentOriginal = parent?.OriginalName ?? id;
    var n = ranges.Count;
    var children = new List<(string ChildId, string Path, string Original, int First, int Last, byte[] Bytes)>();
    // Inherit the reviewer's confirmed orientation onto each child: children are extracted from the parent's UNROTATED
    // bytes, so without this snapshot the human correction would have to be redone per part. source stays "human" (an
    // inherited human orientation still outranks any future auto guess). 0 ⇒ nothing to inherit (children start upright).
    var parentSavedRotation = UploadProcessor.ReadSavedRotationAngle(pdfPath);

    // Reuse the parent's transcript so children skip a fresh vision call: read its RAW per-page markdown (+ legibility)
    // from the review sidecar. Present AND aligned (one entry per page) ⇒ each child inherits its slice via a
    // .pretranscript.json; any absence/mismatch ⇒ children transcribe normally (safe fallback). Barcodes are re-decoded
    // per child downstream (identity safety), so nothing PHI is sliced from the parent here.
    IReadOnlyList<string>? parentPages = null;
    var parentLegibility = 1.0;
    try
    {
        using var sc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(sidecarPath, ct));
        var scr = sc.RootElement;
        if (scr.TryGetProperty("pageMarkdown", out var pm) && pm.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var pages = new List<string>();
            foreach (var el in pm.EnumerateArray()) pages.Add(el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString()! : "");
            if (pages.Count == pageCount) parentPages = pages; // aligned ⇒ sliceable; any mismatch ⇒ leave null (re-transcribe)
        }
        if (scr.TryGetProperty("legibility", out var lg) && lg.ValueKind == System.Text.Json.JsonValueKind.Number) parentLegibility = Math.Clamp(lg.GetDouble(), 0, 1);
    }
    catch { parentPages = null; } // unreadable sidecar ⇒ children transcribe fresh

    // PLAN + EXTRACT first — everything fallible happens BEFORE anything durable. Every child's bytes are carved
    // in memory here, so a malformed range fails as a clean 400 with nothing written, and the fan-out below cannot
    // hit a deterministic extraction error after the commit has landed (which would loop the recovery pass forever).
    for (var i = 0; i < n; i++)
    {
        var (first, last) = ranges[i];
        var childId = UnitKeys.UnitId(id, i + 1, n);
        var childPath = Path.GetFullPath(Path.Combine(inbox, $"{childId}.pdf"));
        // Reviewer-set display name (optional) — used as the child's OriginalName; the child still RE-PROCESSES (fresh
        // classify/match + its own review), only its name is the reviewer's instead of the auto "<parent> (Teil i/n)".
        var childOriginal = body.Names is { } nm && i < nm.Count && !string.IsNullOrWhiteSpace(nm[i])
            ? nm[i].Trim()
            : $"{parentOriginal} (Teil {i + 1}/{n})";
        byte[] childBytes;
        try { childBytes = Adelaide.Ai.PdfPageOps.ExtractRange(parentBytes, first, last); }
        catch (Exception ex) { return Results.BadRequest($"Seiten {first}–{last} konnten nicht extrahiert werden ({ex.GetType().Name}); das Original bleibt unverändert."); }
        children.Add((childId, childPath, childOriginal, first, last, childBytes));
    }

    // INTENT before the commit (C-3: split was the ONE multi-step mutation with no outbox member). The sidecar is
    // the fan-out recovery payload — child ids, ranges, names — NOT the commit: the Segmented event below is the
    // commit, and the recovery pass acts only when that event exists (an intent without it is an aborted commit and
    // is dropped). Written before the append so no ordering of failures can leave a committed split without it.
    var splitIntent = new UploadProcessor.SplitIntent(id, sepVersion, actor,
        children.Select(c => new UploadProcessor.SplitUnit(c.ChildId, c.First, c.Last, c.Original)).ToArray());
    await UploadProcessor.WriteSplitSidecarAsync(pdfPath, splitIntent, ct);

    // COMMIT — the Segmented fan-out on the parent (its terminal state, build-auth D1, and the tamper-evident unit
    // list) — BEFORE the children materialise. The payload names every child, so the children are DERIVED FROM the
    // commit rather than the commit summarising the children: a crash mid-fan-out leaves a terminal parent whose
    // units the recovery pass re-derives from the recorded ranges. The old order (children first, commit last, no
    // intent) let a failed commit leave n children recorded as units of a split that never happened, with the parent
    // still re-filable — the same pages filable n+1 times, and nothing ever re-attempting the commit.
    try
    {
        await audit.AppendAsync(new AuditEntry(id, "Segmented", actor, Details: J(new
        {
            unitCount = n, evidence = "manual", separatorVersion = sepVersion, reviewer = actor,
            units = ranges.Select((r, i) => new { unitId = UnitKeys.UnitId(id, i + 1, n), range = new PageRange(r.First, r.Last).ToString() }).ToArray(),
        })), ct);
    }
    catch (Exception ex)
    {
        // The commit did not (visibly) land: no children exist, the parent is unchanged, and the reviewer can simply
        // retry. The intent is deliberately KEPT for reconcile to adjudicate the torn case — if the append actually
        // landed and only the response path failed, the recovery pass completes the fan-out; if it genuinely did not
        // land, the pass sees no Segmented event and drops the intent.
        return Results.Problem($"Die Aufteilung konnte nicht im Prüfprotokoll verankert werden ({UploadProcessor.SanitizeError(ex)}). Es wurden keine Teile erzeugt — bitte erneut versuchen. (Eine dennoch verankerte Aufteilung wird automatisch vervollständigt.)", statusCode: 502);
    }

    // FAN-OUT — under CancellationToken.None, NOT the request token: the commit above has landed, so from here the
    // work must finish regardless of a browser abort or proxy timeout (B-6); a crash window remains (process kill)
    // and is owned by the split-recovery pass via the intent. Idempotent — a child whose Received already exists
    // (a retried split) is not re-recorded.
    foreach (var c in children)
    {
        await File.WriteAllBytesAsync(c.Path, c.Bytes, CancellationToken.None);
        if (parentSavedRotation != 0) // snapshot the parent's confirmed orientation so the child renders/files upright without re-rotating
            await UploadProcessor.WriteSavedRotationAsync(c.Path, parentSavedRotation, "human", CancellationToken.None);
        if (parentPages is not null) // carry this child's slice of the parent transcript (skips its vision transcription)
        {
            var slice = new List<string>();
            for (var pp = c.First; pp <= c.Last; pp++) slice.Add(parentPages[pp - 1]);
            await UploadProcessor.WritePreTranscriptAsync(c.Path, new UploadProcessor.CarriedTranscript(slice, parentLegibility, id, sepVersion), CancellationToken.None);
        }
        var childSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(c.Bytes)).ToLowerInvariant();
        var derivationKey = UnitKeys.DerivationKey(fileSha, new PageRange(c.First, c.Last), sepVersion);
        if (await store.GetDocumentAsync(c.ChildId, CancellationToken.None) is null)
            await audit.AppendAsync(new AuditEntry(c.ChildId, "Received", actor, Details: J(new
            {
                channel = "Split", sha256 = childSha,
                splitFrom = id, pageRange = new PageRange(c.First, c.Last).ToString(), derivationKey,
            }), Projection: new ProjectionOverrides(OriginalName: c.Original)), CancellationToken.None);
    }

    // Re-enter each child through the full pipeline as a clean single document.
    foreach (var c in children)
        await queue.Writer.WriteAsync(new UploadJob(c.ChildId, c.Path, c.Original), CancellationToken.None);

    // The fan-out completed — the intent is spent. (On any failure above, the sidecar survives and the recovery
    // pass finishes the fan-out from the recorded ranges.)
    UploadProcessor.DeleteSplitSidecar(pdfPath);

    app.Logger.LogInformation("Split {DocId} into {UnitCount} unit(s) by reviewer; children re-enqueued through the pipeline.", id, n);
    // The endpoint APPENDS ONLY (split-parent windowing, the discard treatment): the parent's PDF, review
    // sidecar and saved orientation are the ONLY source to re-derive a mis-split — no durable copy exists (the
    // parent was never filed; the children carry the content but not the bytes) — so they must survive the
    // transition for the repair window. Artifact release belongs to the C5 aged sweep's Segmented branch alone
    // (sole deletion authority; ships separately — under the purge-off posture nothing was deleted here anyway).
    // The parent is terminal (Segmented): the settled-state guards keep it out of every queue and re-entry path.
    return Results.Json(new { ok = true, children = children.Select(c => c.ChildId).ToArray() });
}).RequireCsrfHeader();

// Persist a document's ORIENTATION (net-new): the reviewer rotated the page viewer, and the chosen absolute angle is an
// objective document fact that must survive leaving/re-opening the queue — so it is saved immediately (per click, no
// debounce) to a .rotation.json sidecar beside the inbox PDF, NOT held as per-session view state. This does NOT mutate the
// stored PDF (the bake is a hash-chained transform at filing) and records NO audit event: a pre-commit working-state edit
// like changing a form field — the immutable "Rotated" fact is written only when the submit bakes it. REVIEW-QUEUE ONLY:
// 404 unless an in-review {id}.pdf exists, so an already-filed document can never be "re-oriented". CSRF-gated like every
// browser mutation. The client awaits this PUT's response before submitting, so a bake can never read a stale angle.
app.MapPut("/api/documents/{id}/rotation", async (string id, RotationBody body, IAuditLog audit, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(id) || !IsValidDocId(id)) return Results.BadRequest("invalid documentId");
    var angle = ((body.Angle % 360) + 360) % 360;
    if (angle is not (0 or 90 or 180 or 270)) return Results.BadRequest("angle must be 0, 90, 180 or 270");
    // The terminal guard its sibling routes have (B-4). This PUT had NO state check at all — only the file-existence
    // test below, which under the purge-OFF posture is no gate: a settled document keeps its inputs, so a stale
    // browser tab could save an orientation onto an already-Filed document and be told it succeeded. That saved
    // angle was also C-1's reach onto terminal documents: the next submit-shaped path to touch the doc would bake it.
    var doc = await audit.GetDocumentAsync(id, ct);
    if (doc is not null && DocumentStates.IsSettled(doc.State))
        return Results.Conflict(TerminalConflictText(doc.State));
    var pdfPath = Path.GetFullPath(Path.Combine(inbox, $"{id}.pdf"));
    if (!File.Exists(pdfPath)) return Results.NotFound("no in-review document"); // no captured PDF → nothing to orient
    await UploadProcessor.WriteSavedRotationAsync(pdfPath, angle, "human", ct);
    return Results.Json(new { angle });
}).RequireCsrfHeader();

app.Run();

static string J(object o) => System.Text.Json.JsonSerializer.Serialize(o);

// A document id is 12 hex chars for an uploaded FILE, plus an optional "-u{n}" split-unit suffix (build-auth D1). Fix F:
// the per-endpoint alphanumeric-only check rejected the hyphen, so a split unit's id 400'd on /api/file, /api/review, etc.
static bool IsValidDocId(string? id) => !string.IsNullOrEmpty(id) &&
    System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9]+(-u[0-9]+)?$");

// The EXACT id shape the ledger produces: Guid.NewGuid().ToString("N")[..12] (lowercase hex), plus the optional split-unit
// suffix. Stricter than IsValidDocId, which is deliberately loose for the legacy endpoints. Used by the audit-history read
// so that a mis-cased or over-long id is refused at the door rather than reaching the append-only events table, where the
// two stores disagree (Azure SQL CI collation matches, SQLite BINARY does not) and an oversized parameter loses the index.
static bool IsLedgerDocId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 32 &&
    System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{12}(-u[0-9]+)?$");

// The reviewer-facing 409 text for a stale tab acting on a SETTLED document (DocumentStates.Settled). Each state
// names its own outcome so the reviewer knows whether the document was filed, replaced, discarded or split — a
// generic "already done" would send them hunting.
static string TerminalConflictText(string state) => state switch
{
    "Filed" => "Dokument ist bereits abgelegt.",
    "Superseded" => "Dokument wurde bereits durch ein neueres ersetzt.",
    "Discarded" => "Dokument wurde bereits als Duplikat verworfen.",
    "Segmented" => "Dokument wurde bereits aufgeteilt.",
    "Purged" => "Dokument wurde nach Ablauf der Wiederherstellungsfrist endgültig entfernt.",
    _ => "Dokument ist bereits abgeschlossen.", // SeededHistory (a dedup-seed row: no PDF, nothing to act on)
};

// Resolve a doc's PDF bytes for the page viewer: the intake copy (local, fast — no cache) or the FILED PDF downloaded
// from SharePoint and cached BRIEFLY (bounded IMemoryCache) so the viewer's N page renders don't each re-download. A
// filed doc is one small logical document, so the cached bytes are small + short-lived. Null → not found / no locator.
static async Task<byte[]?> ResolvePdfBytesAsync(string id, string inbox, IAuditStore store, IFiledDocumentStore sink, SharePointOptions spOpt, IMemoryCache cache, Microsoft.Extensions.Logging.ILogger log, CancellationToken ct)
{
    var inboxPath = Path.GetFullPath(Path.Combine(inbox, $"{id}.pdf"));
    if (File.Exists(inboxPath)) return await File.ReadAllBytesAsync(inboxPath, ct); // in-review copy: local, no cache
    var key = "viewerpdf:" + id;
    if (cache.TryGetValue(key, out byte[]? cached) && cached is not null) return cached;
    var doc = await store.GetDocumentAsync(id, ct);
    if (doc is null) return null;
    var locator = FiledLocator.Require(doc, "api/page", log);
    if (locator is null) return null; // tripwire logged → the viewer shows the deleted-original placeholder
    var bytes = await sink.DownloadFiledPdfAsync(locator, await UploadProcessor.RecordedFiledTargetAsync(store, spOpt, id, ct), ct); // the drive the locator was recorded in (C-2)
    if (bytes is not null) cache.Set(key, bytes, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(120), Size = 1 });
    return bytes;
}

// Parse a preview rotate spec "page:deg,page:deg" (1-based page → absolute 0/90/180/270). Ignores malformed/zero entries.
static Dictionary<int, int> ParseRotateSpec(string? spec)
{
    var map = new Dictionary<int, int>();
    if (string.IsNullOrWhiteSpace(spec)) return map;
    foreach (var pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var kv = pair.Split(':');
        if (kv.Length == 2 && int.TryParse(kv[0], out var pg) && int.TryParse(kv[1], out var deg) && pg >= 1)
        {
            var n = ((deg % 360) + 360) % 360;
            if (n is 90 or 180 or 270) map[pg] = n;
        }
    }
    return map;
}

// Re-parse a projection's raw JSON-array string (reasonCodes OR correctedFields) into a string[] for the /api/documents
// DTO (null when the column is null/absent). The value is written by us (Serialize of a string[]), so it's well-formed.
static string[]? ParseReasonCodes(string? json) => string.IsNullOrEmpty(json) ? null : System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
// Re-parse a projection's raw JSON-OBJECT string (contentTypeChange {from,to}) for the DTO; null when the column is null.
static Dictionary<string, string>? ParseObj(string? json) => string.IsNullOrEmpty(json) ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);

// The actor stamped into the immutable audit for a HUMAN action: the authenticated Entra oid (stable, non-PHI). FAIL
// CLOSED — throw if there is no authenticated principal, so a mutation can never be recorded against an anonymous or
// forged "who" in the soon-to-be-immutable ledger. The human endpoints are also behind the Reviewer/Admin authorization,
// so reaching this without a principal is unreachable in practice; the throw is the defence-in-depth tripwire (a 500 is a
// refusal — nothing is written — which is the correct fail-closed outcome).
static string ActorOf(HttpContext http) =>
    EasyAuthDefaults.Actor(http.User) ?? throw new InvalidOperationException("no authenticated actor for an audited action");

/// <summary>
/// Deny-by-default projection of an audit event's <c>Details</c> for the reviewer-facing per-document history
/// (GET /api/documents/{id}/history). Nothing reaches a caller unless it is named in <see cref="Allowed"/>.
///
/// WHY AN ALLOWLIST AND NOT A DENYLIST: the ledger is append-only and most of it PREDATES C6's payload hygiene
/// (shipped 2026-08-08), so the endpoint necessarily serves legacy events. Measured on the localhost ledger, 2,711
/// of 12,735 events carry an identity-bearing key — but those events touch 1,421 of 1,586 documents, i.e. ~90% of
/// documents have at least one. A denylist would have to anticipate every legacy key, and the one that matters most
/// is the one its author would forget: `fields`, an array of {field, was, now, changed} correction triples holding
/// the BEFORE and AFTER values of a corrected patient field. (`kvnr` and `lastName` are not keys anywhere in the
/// ledger — they appear as VALUES of `field`, which is why a naive substring search for them "finds" them.)
///
/// Withheld keys are reported BY NAME (never by value) on each event, so a redacted timeline reads as redacted
/// rather than as an empty one: the caller can see that content exists and go to the ledger itself for it.
/// </summary>
static class DocumentHistory
{
    // Operational vocabulary only: ids, hashes, enums, counts, model/prompt provenance, state transitions.
    //
    // WHAT THIS ALLOWLIST DOES AND DOES NOT GUARANTEE. It matches on the TOP-LEVEL KEY ONLY: an allowed key's value is
    // passed through whole, at whatever depth and shape the writing event chose. Entries were checked against the live
    // ledger for JSON type and size envelope, but that check was a census of events that EXISTED then, so it does not
    // bound what a writer may emit under the same key later. `pages` is the proof that the shapes genuinely vary: it is
    // an INTEGER on `Transcribed` (TranscribedEventDetails) and a DICTIONARY on `Rotated` (page → angle). Both are
    // non-identifying, which is why they stay — but "the shape was verified" would be the wrong thing to conclude, so
    // it is not claimed. A key belongs here only if EVERY writer of it emits non-identifying content, and that is a
    // property of the writers, enforced by review, not by this filter.
    // DELIBERATELY ABSENT, therefore redacted: fileName / oldFileName / duplicateOfName / original (the canonical
    // filename is patient name + insurance number); patient and fields (the pre-C6 was/now correction payloads);
    // error (free-text exception text — every occurrence contains spaces and may embed a patient-named path);
    // sourceRecordId (free-form, up to 65 chars with spaces, provenance unverified — withheld until it isn't).
    //
    // `reasons` IS WITHHELD, though an earlier draft allowed it on the grounds that /api/documents already serves it to
    // this same Reviewer role. That equivalence is false in both directions. (a) The pre-C6 duplicate/gate prose
    // INTERPOLATES ANOTHER DOCUMENT'S OriginalName — e.g. "mögliches Duplikat von „…_Mueller_Hans_A123456789.pdf“" —
    // which is exactly why C6 deleted that line; it would put a DIFFERENT patient's name and insurance number on this
    // document's timeline, on the very event whose `redacted` array reports fileName/original as withheld. (b) The
    // projection's Reasons is single-valued and current-state, and is CLEARED by retry / duplicate-override / reclassify
    // / reconcile re-enqueue — so for any retried pre-C6 document /api/documents returns nothing while the chain still
    // holds every historical string. No post-C6 code path emits a `reasons` key at all (C6 moved prose to the projection
    // side-channel), so withholding it costs nothing current; `reasonCodes` is the structural twin that survives.
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        // classification + review outcome
        "contentType", "originalContentType", "oldContentType", "contentTypeChange", "classifySource",
        "disposition", "outcome", "reasonCodes", "confidence", "correctedFields", "type",
        // intake + content facts
        "pages", "legibility", "barcodes", "channel", "sourceChannel", "sha256", "systemFingerprint",
        // dedup
        "duplicateOf", "acknowledgedDuplicate", "businessDedupKey", "seed",
        // filing + routing  (pdfaDeclared/diRendition are C10's record-of-file facts — post-C6 keys the historical
        // census could not see, and there is no other HTTP surface that answers "how was this filed?")
        "driveItemId", "driveItemIdDeleted", "library", "domain", "domainChange", "domainConfirmed",
        "zielbereich", "patientListItemId", "sharePointItemRemoved", "pdfaDeclared", "diRendition",
        // The write-ahead half of a reclassify names the item it is ABOUT to remove — an opaque Graph id, same class
        // as driveItemId/driveItemIdDeleted. Without it the intent event reads as an unexplained Received.
        "driveItemIdPendingRemoval",
        // supersede lifecycle
        "supersedes", "supersededBy", "reason",
        // splitting / derivation
        "splitFrom", "pageRange", "derivationKey", "separatorVersion", "units", "unitCount", "parent", "source",
        // model + transcript provenance
        "model", "modelVersion", "modelDomain", "promptHash", "reasoningEffort", "transcriptSha256",
        // field-level signals: shapes are {field, signal}, {field, provenance}, {field, agreed, shape} — enums plus a
        // FIELD NAME. Names are now constrained at the extractor to the closed vocabulary (FieldVocabulary), with
        // ReviewDiff.ModelFieldMap re-applying it before the ledger, so newly written events carry catalog names only.
        // The caveat that remains applies to HISTORICAL events this endpoint also serves: a fallback-typed document
        // extracted before that gate shipped could carry a model-authored `column` string, so for those rows "name"
        // is not a hard guarantee of "non-identifying".
        "identityAgreement", "fieldSignals", "fieldProvenance", "evidence",
        // failure + recovery  (errorClass is the exception TYPE NAME — non-PHI, and the only thing in the chain that says
        // why a document dead-lettered; `error`, the free-text message, stays withheld. `recovered` marks a crash-replayed
        // review so a replay is not misread as a genuine second review.)
        "errorClass", "recovered",
        // rotation + misc operational
        "preHash", "postHash", "actor", "backfill",
    };

    /// <summary>Split a Details blob into the allowlisted keys and the NAMES of everything withheld.</summary>
    internal static (Dictionary<string, System.Text.Json.JsonElement> Details, IReadOnlyList<string> Redacted) Project(string? detailsJson)
    {
        var kept = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        var redacted = new List<string>();
        if (string.IsNullOrWhiteSpace(detailsJson)) return (kept, redacted);

        System.Text.Json.JsonDocument doc;
        // A malformed or non-object payload is withheld WHOLE rather than guessed at — fail closed, and say so.
        // Catches ArgumentException as well as JsonException: Parse throws ArgumentException (not JsonException) on
        // undecodable UTF-16 such as a lone surrogate. No current writer can produce that — they all go through
        // JsonSerializer — but the ledger is append-only, so a single bad row arriving from a migration or a hand-repair
        // would otherwise poison that document's history permanently, and escape as a truncated 200 rather than an error.
        try { doc = System.Text.Json.JsonDocument.Parse(detailsJson); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        { return (kept, new[] { "(unparseable)" }); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return (kept, new[] { "(non-object)" });
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (Allowed.Contains(p.Name)) kept[p.Name] = p.Value.Clone(); // Clone: must outlive the JsonDocument
                else redacted.Add(p.Name);
            }
        }
        return (kept, redacted);
    }
}

// Stage III tripwire: a FILED doc MUST carry a driveItemId — Phase 2a backfilled the legacy corpus (class (c) == 0) and
// the write path can't produce an id-less Filed event (FileAsync throws on a missing Graph id). If a retrieval/
// verification/deletion path is ever asked about a doc WITHOUT one, that's an invariant violation: log LOUD (ERROR +
// DocId + state + stage) and return null so the caller fails cleanly — NEVER fall back to the filename (the whole point
// of retiring name-keying). Returns the id when present.
static class FiledLocator
{
    internal static string? Require(DocumentAudit doc, string stage, Microsoft.Extensions.Logging.ILogger log)
    {
        if (!string.IsNullOrEmpty(doc.DriveItemId)) return doc.DriveItemId;
        log.LogError("Doc {DocId} (state={State}) has no driveItemId — id-recording invariant violated at {Stage}; failing clean, NOT falling back to filename.", doc.DocumentId, doc.State, stage);
        return null;
    }
}

// ---- CSRF defence for cookie-authenticated browser mutations (FINDING 2) ----
// The interactive UI authenticates with the Easy Auth SESSION COOKIE, which the browser attaches ambiently — so a
// state-changing endpoint guarded by the cookie alone was reachable by a cross-site request (CSRF). Requiring a custom
// request header closes it: a cross-site <form> cannot set request headers at all, and a cross-site fetch() that adds
// one triggers a CORS preflight this app never answers (no CORS policy is configured), so only a same-origin caller
// gets through. Attach with .RequireCsrfHeader() to EVERY browser-facing mutating endpoint (a new one must add it too).
// Deliberately NOT applied to GET (incl. /api/file, loaded as a navigation/blob fetch) and NOT to the machine drain
// (/api/drain/upload): the drain authenticates with a Bearer token the browser never attaches ambiently, so it is not
// CSRF-susceptible, and requiring this header would break the on-prem drain client (which does not send it).
sealed class CsrfHeaderFilter : IEndpointFilter
{
    internal const string RequiredHeader = "X-Requested-With"; // a header a cross-site form/simple-request cannot set
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var v = ctx.HttpContext.Request.Headers[RequiredHeader];
        if (v.Count == 0 || string.IsNullOrWhiteSpace(v.ToString()))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return await next(ctx);
    }
}

static class MutationEndpointExtensions
{
    /// <summary>Reject the request (403) unless it carries the CSRF header — attach to browser-facing mutations only.</summary>
    public static RouteHandlerBuilder RequireCsrfHeader(this RouteHandlerBuilder b) => b.AddEndpointFilter<CsrfHeaderFilter>();
}

// ---- background processing ----
// B3: provenance of this job — distinguishes a first-pass run from a reconcile re-drive / retry / reclassify, so the
// processing + dead-letter logs can tell "first attempt" from "Nth recovery". Trailing-optional → existing sites = Pipeline.
enum JobOrigin { Pipeline, ReconcileRedrive, Retry, Reclassify }
record UploadJob(string DocId, string Path, string OriginalName, bool ForceReview = false, string? SourceRecordId = null, SourceChannel Channel = SourceChannel.Api, JobOrigin Origin = JobOrigin.Pipeline);

// Process-local striped async lock: serialise callers on a string key with bounded memory (a fixed pool of
// semaphores, no per-key allocation — nothing leaks across a long-running service). DocGate and KeyGate are SEPARATE
// types/instances because the worker holds a per-document lock and a per-business-key lock NESTED; if both keys could
// land on the same semaphore (one shared gate) that nesting would self-deadlock.
class KeyedGate
{
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public async Task<IDisposable> LockAsync(string key, CancellationToken ct)
    {
        var sem = _locks[(int)((uint)key.GetHashCode() % (uint)_locks.Length)];
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }
    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0) sem.Release(); }
    }
}
sealed class DocGate : KeyedGate { } // serialises the brief DuplicateSuspect endpoints (discard / process) for ONE doc id
sealed class KeyGate : KeyedGate { } // serialises the SHA dedup at upload (a short check + append, no slow I/O)

// Non-blocking per-key CLAIM for the FILING paths (worker auto-file + review-submit), where the protected work is a
// multi-second SharePoint upload. Unlike a semaphore held across that upload, a loser fails fast (Acquire().Acquired ==
// false) instead of blocking a thread — and exact-string keys mean no stripe collision can wedge unrelated documents.
// SCOPE: this dictionary is IN-PROCESS. Every mutual-exclusion argument built on it inherits the single-writer
// invariant — exactly ONE app instance (worker_count = 1, pinned at infra/azure/main.tf:95 and restated on
// variables.tf's plan_sku; verified 2026-08-11 against the live plan: B1, capacity 1, no autoscale rules).
//
// What rests on it, concretely — each is a correctness argument, not a performance one:
//   • the C5 aged sweep vs /api/reprocess — losing that race destroys BOTH copies of a document;
//   • /api/split vs the review submit — losing it makes an in-flight filing permanently unrecoverable: the parent
//     lands terminal (Segmented) underneath the filing, and reconcile skips terminal states, so the filing sidecar
//     is never replayed;
//   • the reclassify/supersede/rotate outbox re-drives, which assume one process owns a document's intent at a time.
//
// THE COMPLEMENT, so nobody reads the list above as coverage (B-9): a claim is keyed on ONE document id, so an
// operation spanning two documents — supersede: successor + predecessor — must take BOTH, in that order; the review
// submit and the reconcile supersede re-drive now do (B-2: before that, /api/reprocess could revive a predecessor
// mid-supersede and a terminal Superseded then landed on a live document). Split's children claim different keys
// ({id}-u{n}) by construction. Four routes hold NO FilingClaims claim at all: /api/retry (its own retryGate),
// /api/duplicate-discard and /api/duplicate-process (DocGate — a DIFFERENT singleton, invisible to this one; the
// discard-vs-submit window B-3 lives there and is NOT closed by anything here), and PUT /api/documents/{id}/rotation
// (state-guarded since B-4, still unclaimed — it writes a sidecar, not the ledger).
//
// SCALING UP (B1 → B2) IS SAFE and is what the migration plan intends — capacity stays 1. SCALING OUT IS NOT, and
// the host mutex does NOT save you: `Global\Adelaide.Web` is MACHINE-scoped, so two instances on two machines both
// start cleanly and neither sees the other's claims. Terraform pins the count but cannot prevent a Portal/CLI
// scale-out between applies (the next apply would revert it, after the damage). If Adelaide ever scales out, these
// claims must become distributed (or the deletion paths re-serialized) BEFORE the second instance starts.
sealed class FilingClaims
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _held = new();
    /// <summary>Try to claim a key; dispose the handle to release. Check <see cref="Claim.Acquired"/> before proceeding.</summary>
    public Claim Acquire(string key) => new(_held.TryAdd(key, 0) ? this : null, key);
    private void Release(string key) => _held.TryRemove(key, out _);
    public readonly struct Claim(FilingClaims? owner, string key) : IDisposable
    {
        public bool Acquired => owner is not null;       // false when another path already holds it (or for a default handle)
        public void Dispose() => owner?.Release(key);    // releases only what THIS handle actually claimed
    }
}

// `transcripts` is the always-on capture store at runtime (docs/CAPTURE.md); it is nullable ONLY so tests can
// construct the processor without one — and because processing must survive a broken capture store either way.
// `health` counts capture failures so fail-open never means fail-silent.
sealed class UploadProcessor(Channel<UploadJob> queue, IServiceScopeFactory scopes, IAuditLog audit, IAuditStore store, FilingClaims claims, ILogger<UploadProcessor> log, Adelaide.Audit.ITranscriptStore? transcripts = null, CaptureHealth? health = null, SweepHealth? sweepHealth = null) : BackgroundService
{
    // Each document is I/O-bound (mostly waiting on Azure OpenAI/DI/Graph), so a few in parallel multiplies throughput;
    // the cap protects those services' rate limits (429s are still absorbed by the resilience handlers). Tune via env.
    private static readonly int Concurrency =
        int.TryParse(Environment.GetEnvironmentVariable("ADELAIDE_CONCURRENCY"), out var n) && n is > 0 and <= 32 ? n : 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReapOrphanSidecarsAsync(stoppingToken); // drop filing sidecars whose doc never reached / has left "Filing"
        await PurgeCaptureWindowAsync(stoppingToken);  // age out captured content past the configured window (no-op unless window + PurgeGate)
        await PurgeAgedFiledInputsAsync(stoppingToken); // C5: release inbox inputs of docs filed+verified past the grace window (no-op unless window + PurgeGate)
        await PurgeAgedDiscardedAsync(stoppingToken);  // discard-recovery: seal Discarded docs past the recovery window (no-op unless PurgeGate)
        await ReconcileAsync(stoppingToken);           // startup: full sweep — the in-memory queue is empty, so re-enqueue all stranded work
        // N workers draw from the single channel (channels are multi-reader); the audit's own append gate keeps the hash
        // chain consistent across them. A periodic Filing-only sweep runs alongside so an in-doubt filing left by a
        // mid-FileAsync failure is re-driven without waiting for the next restart.
        var workers = Enumerable.Range(0, Concurrency).Select(_ => ConsumeAsync(stoppingToken)).ToList();
        workers.Add(PeriodicReconcileAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    // The outbox contract leaves a failed filing in "Filing" (never dead-lettered), to be completed from its sidecar.
    // ReconcileAsync runs at startup, but a failure during normal operation needs recovery without a restart — so sweep
    // periodically for in-doubt filings. Filing-ONLY: live pipeline docs (Received…Matched) are normally in flight and
    // must not be re-enqueued from here; only a doc the claim shows is unclaimed AND still "Filing" is genuinely in doubt.
    private async Task PeriodicReconcileAsync(CancellationToken ct)
    {
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("ADELAIDE_RECONCILE_MINUTES"), out var m) && m is > 0 and <= 1440 ? m : 2;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes)); // waits for each tick → passes never overlap (non-reentrant)
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await ReconcileAsync(ct, filingOnly: true);
                await PurgeCaptureWindowAsync(ct); // the window purge rides the same non-reentrant tick — no extra timer
                await PurgeAgedFiledInputsAsync(ct); // C5: so does the grace-window sweep
                await PurgeAgedDiscardedAsync(ct); // discard-recovery: and the recovery-window sweep
                // Heartbeat, stamped even when the pass did nothing: this is what makes a healthy idle sweep
                // distinguishable from a dead timer on /api/stats. All four calls above swallow their own
                // exceptions, so reaching here means the pass completed, not that it found work.
                sweepHealth?.MarkTick(DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>C5 (D4): the grace-window sweep — release the inbox inputs of documents whose FILING is older than
    /// ADELAIDE_FILED_PURGE_GRACE_DAYS. Only meaningful when the window is configured (otherwise the at-filing purge
    /// owns deletion) AND the PurgeGate arms deletion; both closed → no-op. Same verify-before-delete core as the
    /// at-filing purge: nothing is deleted unless the item is confirmed in SharePoint by its driveItemId.</summary>
    private Task PurgeAgedFiledInputsAsync(CancellationToken ct) =>
        PurgeAgedFiledInputsAsync(PurgeGate.Enabled, FiledPurgeGrace.Days, ct);

    /// <summary>Gate-explicit overload (the gates are read-once statics; tests inject the decisions here).</summary>
    internal async Task PurgeAgedFiledInputsAsync(bool purgeEnabled, int graceDays, CancellationToken ct)
    {
        if (!purgeEnabled || graceDays <= 0) return;
        using var scope = scopes.CreateScope();
        var sink = scope.ServiceProvider.GetRequiredService<IFiler>(); // same SharePointSink at runtime; IFiler is the test seam
        var spOptions = scope.ServiceProvider.GetService<Adelaide.SharePoint.SharePointOptions>(); // null in bare test hosts → medical-default resolution
        await PurgeAgedFiledInputsCoreAsync(sink, spOptions, graceDays, DateTimeOffset.UtcNow, ct);
    }

    /// <summary>The gate-free sweep core (internal for tests — callers apply the gates). Enumerates the inbox with a
    /// cheap mtime prefilter; the LEDGER decides which docs are past the window, and the per-state switch decides the
    /// release condition: Filed (with a driveItemId) and Superseded (via the filed successor) re-verify against
    /// SharePoint before deleting; Segmented releases window-only — a split parent has no durable copy to verify, and
    /// its split intent, while present, keeps everything (the crash-recovery pass derives children from the PDF).
    /// The per-doc claim excludes every live path that holds "doc:"-keyed claims: the worker, review
    /// submit, the reaper, reconcile — and /api/reprocess, which acquires the claim precisely so this sweep can
    /// never race its rehydrate-then-delete against a purge (both copies could otherwise be destroyed).</summary>
    internal async Task PurgeAgedFiledInputsCoreAsync(IFiler sink, Adelaide.SharePoint.SharePointOptions? spOptions, int graceDays, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var inboxDir = Environment.GetEnvironmentVariable("ADELAIDE_INBOX") ?? "inbox";
            if (!Directory.Exists(inboxDir)) return;
            var cutoff = now.AddDays(-graceDays);
            var purged = 0;
            foreach (var path in Directory.EnumerateFiles(inboxDir, "*.pdf"))
            {
                ct.ThrowIfCancellationRequested();
                if (File.GetLastWriteTimeUtc(path) > cutoff.UtcDateTime) continue; // captured before filing → an aged filing implies an aged file
                var docId = Path.GetFileNameWithoutExtension(path);
                using var claim = claims.Acquire("doc:" + docId);
                if (!claim.Acquired) continue; // a live path holds it — not this sweep's to touch
                var doc = await audit.GetDocumentAsync(docId, ct);
                if (doc is null || doc.LastUpdated > cutoff) continue; // the LEDGER grants the window — from the filing, or from the supersede (the corrective event)
                var released = doc switch
                {
                    // Verify in the drive each locator was RECORDED in (C-2): the doc's own Filed destination for
                    // the Filed branch, the SUCCESSOR's Filed destination for the Superseded branch — before this,
                    // a business filing was probed on the medical drive, read as absent, and its inputs never released.
                    { State: "Filed", DriveItemId.Length: > 0 } =>
                        await PurgeFiledInputsCoreAsync(sink, docId, path, doc.DriveItemId!,
                            await RecordedFiledTargetAsync(audit, spOptions, docId, ct), log, ct),
                    // Superseded within the window: the old item is INTENTIONALLY gone from SharePoint, so the
                    // verify runs against the SUCCESSOR's driveItemId — the content this document's arrival copy
                    // insured lives on there. Without this branch a grace-deferred superseded input would linger
                    // forever (verification finding); WITH it, a supersede-in-error keeps its arrival copy for the
                    // same window an ordinary misfile gets.
                    { State: "Superseded" } when await SupersededByFiledDriveItemAsync(docId, ct) is { } successor =>
                        await PurgeFiledInputsCoreAsync(sink, docId, path, successor.DriveItemId,
                            await RecordedFiledTargetAsync(audit, spOptions, successor.DocId, ct), log, ct),
                    // Segmented (split-parent windowing, 2026-08-16): the parent's inputs are the only mis-split
                    // repair source, and NO durable copy exists to verify against — the parent was never filed,
                    // and the children carry the content but not the bytes. So the release is WINDOW-ONLY, no
                    // verify (decision D2): the human who performed the split is the authority, the grace window
                    // is the repair period, and a children-are-Filed verify would recreate the parked-forever
                    // silence the census documents on the Superseded branch's chain-keeps. The in-doubt guard is
                    // the SPLIT INTENT: while {id}.split.json survives, the crash-recovery pass still derives
                    // children from this PDF — keep everything.
                    { State: "Segmented" } when !File.Exists(SplitSidecarPath(path)) =>
                        ReleaseSplitParentInputs(docId, path, log),
                    _ => false,
                };
                if (released)
                    purged++; // count only REAL deletes — keep/skip paths must not inflate the release log
            }
            if (purged > 0) log.LogInformation("Aged filed-input purge: released inbox inputs for {Count} document(s) settled more than {Days}d ago (Filed/Superseded verified in SharePoint; Segmented window-only).", purged, graceDays);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { log.LogWarning("Aged filed-input purge sweep failed ({ExType}); retried on the next reconcile tick.", ex.GetType().Name); }
    }

    /// <summary>The FILED successor's driveItemId — AND its doc id, so the caller can resolve the drive that locator
    /// was recorded in (C-2) — for a Superseded doc, walking `supersededBy` TRANSITIVELY
    /// (second verification round: re-scans of one paper document supersede in series, so A→B→C is a normal chain;
    /// resolving only the direct successor left A's input stranded forever once B was itself superseded). Bounded
    /// and cycle-guarded. Null on any doubt — a Discarded/missing terminal, a malformed event, a broken chain —
    /// and the sweep then keeps the old input (fail-safe, retried next tick).</summary>
    private async Task<(string DriveItemId, string DocId)?> SupersededByFiledDriveItemAsync(string docId, CancellationToken ct)
    {
        try
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { docId };
            var current = docId;
            for (var hop = 0; hop < 10; hop++)
            {
                var next = await SupersededByAsync(current, ct);
                if (next is null || !visited.Add(next)) return null;
                var successor = await audit.GetDocumentAsync(next, ct);
                if (successor is { State: "Filed", DriveItemId.Length: > 0 }) return (successor.DriveItemId!, next);
                if (successor is not { State: "Superseded" }) return null; // Discarded/missing → the old copy is the only surviving artifact
                current = next;
            }
            return null;
        }
        catch { return null; } // malformed event / store hiccup → keep the copy
    }

    private async Task<string?> SupersededByAsync(string docId, CancellationToken ct)
    {
        foreach (var e in (await audit.GetEventsAsync(docId, ct)).Reverse())
        {
            if (e.Type != "Superseded") continue;
            using var d = System.Text.Json.JsonDocument.Parse(e.Details);
            return d.RootElement.TryGetProperty("supersededBy", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        return null;
    }

    /// <summary>Every discard artifact the recovery window preserves and the sweep therefore releases: the
    /// captured PDF plus all three sidecars a review-lane document can carry.</summary>
    private static string[] DiscardArtifactPaths(string inboxDir, string docId) =>
        [Path.Combine(inboxDir, $"{docId}.pdf"), Path.Combine(inboxDir, $"{docId}.review.json"),
         Path.Combine(inboxDir, $"{docId}.rotation.json"), Path.Combine(inboxDir, $"{docId}.pretranscript.json")];

    /// <summary>Every discard from the endpoint's birth (2026-06-10) until this commit landed (`1ccc4e7`) ran an
    /// unconditional inline delete, so a pre-kill-switch Discarded row without artifacts is ordinary history. A
    /// POST-kill-switch one is impossible except after a crash mid-sweep — or an unknown deleter, which is
    /// exactly what the recovery window exists to never meet silently. Hence the WARNING keyed on this instant.</summary>
    private static readonly DateTimeOffset KillSwitchInstant = DateTimeOffset.Parse("2026-07-27T14:36:03+00:00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Discard-recovery sweep: Discarded → Purged after the recovery window (delete-then-append), plus
    /// the sealed-leftover cleanup. Gates: the PurgeGate kill-switch must be armed; the window is ALWAYS
    /// configured (DiscardPurgeGrace: unset parses to 30 — arming-implies-a-window), and an explicit 0 is VALID,
    /// meaning a zero-day window: everything eligible on the very next tick. Deliberately NOT "non-zero to run" —
    /// 0 must purge, not park artifacts ownerless forever.</summary>
    private Task PurgeAgedDiscardedAsync(CancellationToken ct) =>
        PurgeAgedDiscardedAsync(PurgeGate.Enabled, DiscardPurgeGrace.Days, DateTimeOffset.UtcNow, ct);

    /// <summary>Gate-explicit overload (the gates are read-once statics; tests inject the decisions and the
    /// clock — the window check compares the CALLER's `now`, reducers never read time).
    ///
    /// LEDGER-enumerated, never the inbox: an artifact-less Discarded row (historically: every pre-kill-switch
    /// discard; in the future: a crash between delete and append) is invisible to a file-driven loop and would
    /// stay recoverable-looking forever. Per document: claim (skip on contention — /api/undiscard holds the same
    /// key), re-read under the claim, then delete-then-append: artifacts first, `Purged` only after every one is
    /// released; a failed delete keeps the document Discarded and retries next tick, and a crash between the
    /// deletes and the append self-heals through the artifact-less branch.</summary>
    internal async Task PurgeAgedDiscardedAsync(bool purgeEnabled, int graceDays, DateTimeOffset now, CancellationToken ct)
    {
        if (!purgeEnabled) return; // evidence-preservation kill-switch closed → the sweep ships dark (undiscard still works: nothing is deleting)
        try
        {
            var inboxDir = Environment.GetEnvironmentVariable("ADELAIDE_INBOX") ?? "inbox";
            int sealedCount = 0, leftoverCount = 0;
            foreach (var candidate in await store.ListByStateAsync("Discarded", limit: 100_000, ct))
            {
                ct.ThrowIfCancellationRequested();
                using var claim = claims.Acquire("doc:" + candidate.DocumentId);
                if (!claim.Acquired) continue; // a live path holds it (undiscard mid-flight above all) — not this sweep's to touch
                var cur = await audit.GetDocumentAsync(candidate.DocumentId, ct);
                if (cur is not { State: "Discarded" }) continue; // undiscarded (or otherwise moved) between the list and the claim
                // LastUpdated IS the latest Discarded event's At: State=="Discarded" means the newest advancing
                // event is that discard, and annotations deliberately do not move LastUpdated.
                var discardedAt = cur.LastUpdated;
                var artifacts = DiscardArtifactPaths(inboxDir, cur.DocumentId);
                if (Array.Exists(artifacts, File.Exists))
                {
                    if (now <= discardedAt.AddDays(graceDays)) continue; // in-window with artifacts → recoverable; same boundary arithmetic as UndiscardGuard
                    var failed = 0;
                    foreach (var path in artifacts)
                    {
                        try { File.Delete(path); } // Delete on a missing path is a no-op — missing counts as released
                        catch (Exception delEx)
                        {
                            failed++;
                            log.LogWarning("Discard sweep could not delete an artifact of {DocId} ({Error}); the document stays Discarded and is retried next tick.", cur.DocumentId, delEx.GetType().Name);
                        }
                    }
                    if (failed > 0) continue; // keep Discarded — Purged may only land once every artifact is released
                }
                else if (discardedAt > KillSwitchInstant)
                {
                    // Artifact-less: recovery is already impossible, so the window would be a fiction — seal now
                    // regardless of age. Post-kill-switch absence is the impossible state (see KillSwitchInstant).
                    log.LogWarning("Discard sweep: {DocId} was discarded {DiscardedAt:o} — after the kill-switch — yet has no artifacts. A crash mid-sweep, or an unknown deleter; sealing it now.", cur.DocumentId, discardedAt);
                }
                await audit.AppendAsync(new AuditEntry(cur.DocumentId, "Purged", "discard-sweep",
                    Details: $"{{\"graceDays\":{graceDays}}}"), ct);
                sealedCount++;
            }
            // SEALED-LEFTOVER cleanup: a Purged row that still has artifacts on disk. The one-time seal of
            // 2026-08-15 created exactly 4 of these; delete-then-append ordering can create none. Files are
            // released with NO append — and that is correct ONLY here: the row is already sealed (`Purged`
            // accepts no events; the store guard would reject the append, and there is no state change left to
            // record), while leaving the files would recreate the census's orphaned-artifact gap. Do NOT copy
            // this delete-without-append shape anywhere a state transition still has to happen.
            foreach (var candidate in await store.ListByStateAsync("Purged", limit: 100_000, ct))
            {
                ct.ThrowIfCancellationRequested();
                var leftovers = DiscardArtifactPaths(inboxDir, candidate.DocumentId);
                if (!Array.Exists(leftovers, File.Exists)) continue;
                using var claim = claims.Acquire("doc:" + candidate.DocumentId);
                if (!claim.Acquired) continue;
                var cur = await audit.GetDocumentAsync(candidate.DocumentId, ct);
                if (cur is not { State: "Purged" }) continue;
                var released = false;
                foreach (var path in leftovers)
                {
                    try { if (File.Exists(path)) { File.Delete(path); released = true; } }
                    catch (Exception delEx) { log.LogWarning("Discard sweep could not release a leftover artifact of sealed {DocId} ({Error}); retried next tick.", cur.DocumentId, delEx.GetType().Name); }
                }
                if (released) leftoverCount++;
            }
            if (sealedCount > 0 || leftoverCount > 0)
                log.LogInformation("Discard sweep: sealed {Sealed} document(s) past the {Days}d recovery window; released leftover artifacts of {Leftover} already-sealed document(s).", sealedCount, graceDays, leftoverCount);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { log.LogWarning("Discard sweep failed ({ExType}); retried on the next reconcile tick.", ex.GetType().Name); }
    }

    /// <summary>The deletion half of the capture semantics (docs/CAPTURE.md): age out captured content past
    /// ADELAIDE_TRANSCRIPT_RETENTION_DAYS. Two gates must BOTH be open — the window must be configured AND the
    /// evidence-preservation kill-switch (PurgeGate) must allow deletion — so "capture everything" can never be
    /// silently undone by a lone env var while an incident freeze is in force. Failures are counted, never thrown:
    /// a broken purge must not take down the worker.</summary>
    private async Task PurgeCaptureWindowAsync(CancellationToken ct)
    {
        if (transcripts is null || !TranscriptRetentionGate.Enabled) return; // no store (tests) or no window configured
        if (!PurgeGate.Enabled)
        {
            log.LogDebug("Capture window purge held: window={Days}d configured but ADELAIDE_PURGE_ENABLED is not set.", TranscriptRetentionGate.Policy.RetentionDays);
            return;
        }
        try
        {
            var removed = await transcripts.PurgeOlderThanAsync(TranscriptRetentionGate.Policy.CutoffFrom(DateTimeOffset.UtcNow), ct);
            if (removed > 0)
                log.LogInformation("Capture window purge removed {Removed} transcript(s) (plus artifacts/model calls/adjudicated values; corpus measurement rows are exempt per C2) older than {Days}d.",
                    removed, TranscriptRetentionGate.Policy.RetentionDays);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            health?.CountFailure("purge");
            log.LogWarning("Capture window purge failed ({ExType}); captured content is retained until the next attempt.", ex.GetType().Name);
        }
    }

    // PHI hygiene: a filing sidecar is the recovery payload for an in-doubt filing — it holds the transcript. Reconcile
    // completes ANY sidecar-bearing doc (the sidecar's existence, not the state label, marks an in-doubt filing), so the
    // reaper must reap only SETTLED docs: the full inherited terminal set (DocumentStates.Settled — a hand-written
    // four-state list here once omitted Segmented and SeededHistory, so a Segmented parent's sidecar was enumerated on
    // every sweep and skipped forever, an immortal PHI-bearing file the D-1 gate fix would otherwise strand), plus
    // DeadLetter (not terminal — a parked doc is revivable via retry — but settled for SIDECAR purposes: a retry
    // reprocesses from scratch and never replays a stale filing intent), plus no audit record at all. Anything still
    // in flight is kept for reconcile to finish. Claim each first so it never deletes a sidecar a live path is
    // mid-writing. (Internal for the reaper test; production calls it from startup only.)
    internal async Task ReapOrphanSidecarsAsync(CancellationToken ct)
    {
        try
        {
            var inboxDir = Environment.GetEnvironmentVariable("ADELAIDE_INBOX") ?? "inbox";
            if (!Directory.Exists(inboxDir)) return;
            const string suffix = ".filing.json";
            int reaped = 0;
            foreach (var path in Directory.EnumerateFiles(inboxDir, "*" + suffix))
            {
                var name = Path.GetFileName(path);
                if (name.Length <= suffix.Length) continue;
                using var claim = claims.Acquire("doc:" + name[..^suffix.Length]);
                if (!claim.Acquired) continue;                   // a live path holds it → mid-write, leave it
                var doc = await audit.GetDocumentAsync(name[..^suffix.Length], ct);
                if (doc is not null && !DocumentStates.IsSettled(doc.State) && doc.State != "DeadLetter") continue; // still completable → keep; settled = the inherited terminal set + DeadLetter (see the header comment)
                try { File.Delete(path); reaped++; } catch (Exception delEx) { log.LogWarning("Reaper could not delete settled filing sidecar for {DocId} ({Error}); it remains on disk.", name[..^suffix.Length], delEx.GetType().Name); }
            }
            if (reaped > 0) log.LogInformation("Reaped {Count} settled filing sidecar(s) on startup.", reaped);
        }
        catch (Exception ex) { log.LogWarning("Filing-sidecar reaper failed; continuing. {ExType} [{ExChain}]", ex.GetType().Name, Redact.Chain(ex)); }
    }

    internal async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(ct))
        {
            // A2: ambient DocId for every line in this loop — incl. the failure log below, which runs OUTSIDE ProcessAsync's
            // own DocId scope (gone once ProcessAsync throws), so the dead-letter line stays DocId-correlated.
            using var jobScope = log.BeginScope(new Dictionary<string, object> { ["DocId"] = job.DocId });
            // Claim the document for its whole lifecycle (atomic, non-blocking): a double-enqueue, a second worker, or a
            // racing review-submit/discard all fail the claim and back off rather than double-processing or -filing.
            using var docClaim = claims.Acquire("doc:" + job.DocId);
            if (!docClaim.Acquired)
            {
                log.LogWarning("Skipping {DocId}: already being processed or filed by another path.", job.DocId);
                continue;
            }
            // Durable attempt marker (deletable store, fail-open): the redrive bound parks a document only when
            // ATTEMPTS since its last real ingest hit the limit. Without this row a document merely queued through
            // N routine restarts is indistinguishable in the ledger from one that kills the worker on every pickup
            // — a crash before the first pipeline event leaves no other durable trace that processing began.
            try { if (transcripts is not null) await transcripts.MarkAttemptStartAsync(job.DocId, DateTimeOffset.UtcNow, ct); }
            catch (Exception markEx) when (markEx is not OperationCanceledException)
            {
                log.LogDebug("Attempt-start mark failed for {DocId} ({ExType}); the redrive bound stays fail-open for this attempt.", job.DocId, markEx.GetType().Name);
            }
            try { await ProcessAsync(job, ct); }
            catch (Exception ex)
            {
                log.LogError("Processing failed for {DocId} at stage={Stage} (last good state {State}, origin {Origin}); {ExType} [{ExChain}] httpStatus={HttpStatus} graphReqId={GraphRequestId}",
                    job.DocId, ex.Data["stage"], ex.Data["state"], job.Origin, ex.GetType().Name, Redact.Chain(ex), ex.Data["httpStatus"], ex.Data["graphRequestId"]);
                // Tag throttling distinctly. A migration lands on a NEW deployment with its own TPM quota, so 429s are
                // the expected first symptom of an under-provisioned target — and without a code they read as generic
                // processing failures, indistinguishable from a genuine model regression in the very window where that
                // distinction matters most. Same shape as the malware_blocked tag above; routing is unchanged.
                // An EMPTY model response is tagged just as distinctly, and for the same reason: it means the completion
                // budget is too small for the configured reasoning effort — a configuration error that retrying
                // identically cannot fix. Untagged it reads as a generic processing failure, which is exactly how a
                // naive cutover would have looked: every document dead-lettering with a JSON parse error.
                var reasonCodes =
                    ex is Adelaide.Ai.EmptyModelResponseException || ex.InnerException is Adelaide.Ai.EmptyModelResponseException
                        ? new[] { ReasonCodes.ModelEmptyResponse }
                    : ex.Data["httpStatus"] is 429 ? new[] { ReasonCodes.RateLimited }
                    : Array.Empty<string>();
                // C6: the chain records the exception CLASS + codes; the sanitized message (still potentially quoting
                // upstream response bodies) goes to the erasable projection and the log — never the append-only payload.
                try { await audit.AppendAsync(new AuditEntry(job.DocId, "DeadLetter", "worker", Details: Json(new { errorClass = ex.GetType().Name, reasonCodes }),
                    Projection: new ProjectionOverrides(Reasons: SanitizeError(ex))), ct); }
                catch (Exception deadLetterEx)
                {
                    // The DeadLetter append itself failed: the doc stays non-terminal, so reconcile keeps re-enqueuing it
                    // (a silent retry loop). Surface it. LOG ONLY -- deliberately no retry bound / terminal-state change here.
                    log.LogError(deadLetterEx, "DeadLetter append failed for {DocId}; not recorded as DeadLetter, so reconcile will re-enqueue it (possible retry loop).", job.DocId);
                }
            }
        }
    }

    // The dashboard shows this string; upstream exceptions embed full HTTP response bodies (which can echo PHI from
    // filter/filename values) — keep only the short head here, the full exception stays in the server log.
    internal static string SanitizeError(Exception ex) => Redact.Error(ex); // PHI-safe head; shared with all log/error rendering

    // The job queue is in-memory: a restart drops queued/in-flight work, leaving documents in a non-terminal state with
    // their PDF still captured. Re-enqueue them so nothing is ever stuck "in Bearbeitung" forever, and COMPLETE any
    // in-doubt "Filing" doc from its sidecar. filingOnly=true (the periodic sweep) does ONLY the latter — re-enqueuing
    // live pipeline work mid-run would race active workers; completing an unclaimed in-doubt filing is always safe.
    internal async Task ReconcileAsync(CancellationToken ct, bool filingOnly = false)
    {
        try
        {
            var inboxDir = Environment.GetEnvironmentVariable("ADELAIDE_INBOX") ?? "inbox";
            using var scope = scopes.CreateScope();
            var sink = scope.ServiceProvider.GetRequiredService<IFiler>(); // same SharePointSink at runtime (DI); IFiler is the test seam
            int reEnqueued = 0, recovered = 0, supersededRecovered = 0, reclassifyRecovered = 0;

            // ALL in-flight docs (uncapped) — a restart during a large backlog must re-drive everything the in-memory
            // queue lost. A doc stuck in "Filing" is the dual-write in-doubt case (crashed between the SharePoint upload
            // and recording "Filed"): COMPLETE it from the recorded intent (the deletable sidecar) — re-drive the spec
            // idempotently to the recorded path (PUT-replace + PATCH-all + Volltext). No probe, no "is it complete?"
            // inference; re-running the spec converges any partial state (bytes-only, half-metadata) to one full item.
            // State-based candidates: in-flight docs (excludes terminal states AND awaiting-human NeedsReview). UNCHANGED.
            var candidates = (await store.ListReconcilableAsync(ct: ct)).ToList();

            // PLUS the orphan-Filing-sidecar recovery -- KEYED ON THE SIDECAR FILE, not on NeedsReview state. The
            // review-submit edge (sidecar written, then the "Filing" append failed) leaves the doc in NeedsReview with its
            // .filing.json on disk. Enumerate the .filing.json files (NOT the NeedsReview population) and add only those
            // whose doc is a NeedsReview not already a candidate. A doc awaiting a human has a .review.json but NO
            // .filing.json -- it is never enumerated -> never added -> untouched. A stale .filing.json on a Filed/other
            // doc IS enumerated, but the State == "NeedsReview" check keeps it out: only genuine orphans are recovered.
            const string filingSidecarSuffix = ".filing.json";
            if (Directory.Exists(inboxDir))
            {
                foreach (var sidecar in Directory.EnumerateFiles(inboxDir, "*" + filingSidecarSuffix))
                {
                    var sidecarName = Path.GetFileName(sidecar);
                    if (sidecarName.Length <= filingSidecarSuffix.Length) continue;
                    var sidecarDocId = sidecarName[..^filingSidecarSuffix.Length];
                    if (candidates.Any(c => c.DocumentId == sidecarDocId)) continue;   // already a candidate (e.g. Filing) -- no double-handle
                    var orphan = await store.GetDocumentAsync(sidecarDocId, ct);
                    if (orphan is { State: "NeedsReview" }) candidates.Add(orphan);    // orphan Filing sidecar on a NeedsReview doc => in-doubt
                }
            }

            foreach (var d in candidates)
            {
                // Claim FIRST for every candidate: if a worker is actively processing/filing this doc it holds the claim,
                // so we skip and leave it alone. Then re-read under the claim — the snapshot from the list query may be
                // stale (the doc just advanced or finished). This guards both branches below against racing a live worker.
                using var claim = claims.Acquire("doc:" + d.DocumentId);
                if (!claim.Acquired) continue;
                var cur = await audit.GetDocumentAsync(d.DocumentId, ct);
                if (cur is null) continue;
                // Settled under us between the list query and the claim — the FULL terminal set, inherited (D-1: this
                // gate tested "Filed" alone, one of five, while the list-time gate excluded all five; a document that
                // turned Discarded/Superseded/Segmented/SeededHistory in the window fell through to the upload below
                // and a terminal "Filed" was appended OVER the disposition — reconcile filing a document a human just
                // disposed of, invisibly, because the end state is terminal and every detector stops looking). The
                // sidecar is deliberately KEPT for the startup reaper, which takes the same inherited set.
                if (DocumentStates.IsSettled(cur.State)) continue;
                var pdfPath = Path.GetFullPath(Path.Combine(inboxDir, $"{d.DocumentId}.pdf"));
                var rec = ReadFilingSidecar(pdfPath);
                if (rec is not null)
                {
                    // A filing sidecar present = an in-doubt filing: the doc reached the filing step and the upload+record
                    // may be incomplete. Complete from intent regardless of the STATE LABEL — "Filing", or a post-upload
                    // intermediate ("Reviewed") left by a crash between appends. Re-driving the spec is idempotent
                    // (PUT-replace + PATCH-all), so any partial state converges to one full item at the recorded path.
                    try
                    {
                        var filing = await sink.FileAsync(rec.Spec, ct);
                        var driveItemId = filing.DriveItemId;
                        if (rec.Reviewer is not null) // review-origin: keep the human-correction diff + true attribution through recovery
                        {
                            // Mark this "Reviewed" as a recovery replay (recovered=true) so a metric consumer can dedup it
                            // against an inline "Reviewed" that may already exist (crash between the inline Reviewed and Filed).
                            var reviewed = System.Text.Json.Nodes.JsonNode.Parse(rec.ReviewDiffJson ?? "{}")!.AsObject();
                            // This append is a NEW write to the immutable chain, not a replay of an existing row, so
                            // the sidecar's field names must clear the same vocabulary as every other door. A sidecar
                            // written BEFORE the extractor gate shipped still sits on disk carrying whatever the model
                            // authored; without this, recovery would put it in the ledger after the fix.
                            var droppedNames = ReviewDiff.SanitizeStoredDiff(reviewed);
                            if (droppedNames > 0)
                                log.LogWarning("Reconcile: dropped {Count} off-vocabulary field name(s) from a pre-gate review sidecar for {DocId} before the recovery append. Count only — a model-authored column name can be arbitrary text.", droppedNames, d.DocumentId);
                            reviewed["recovered"] = true;
                            await audit.AppendAsync(new AuditEntry(d.DocumentId, "Reviewed", rec.Reviewer, Details: reviewed.ToJsonString()), ct);
                        }
                        // The RECORDED DESTINATION must survive recovery (C-2): RecordedFiledTargetAsync reads the LAST
                        // Filed event's domain/library markers, and this arm is the fourth Filed writer. A recovered
                        // BUSINESS filing (spec target with RequiresPatient=false — today's one non-medical shape)
                        // without the marker would resolve as medical for every later by-id operation — re-creating
                        // the wrong-drive defect for precisely the crash-window documents this outbox protects.
                        object recoveredFiled = rec.Spec.Target is { RequiresPatient: false } recTarget
                            ? new { contentType = rec.Spec.ContentType, domain = DocumentDomain.Business, siteId = recTarget.SiteId, library = recTarget.Library,
                                disposition = rec.Reviewer is null ? "AutoFile" : "ReviewedThenFiled", acknowledgedDuplicate = rec.AcknowledgedDuplicate, recovered = true, driveItemId,
                                pdfaDeclared = filing.PdfADeclared, diRendition = filing.DiRendition }
                            : new { contentType = rec.Spec.ContentType, patientListItemId = rec.PatientLookupId,
                                disposition = rec.Reviewer is null ? "AutoFile" : "ReviewedThenFiled", acknowledgedDuplicate = rec.AcknowledgedDuplicate, recovered = true, driveItemId,
                                pdfaDeclared = filing.PdfADeclared, diRendition = filing.DiRendition };
                        await audit.AppendAsync(new AuditEntry(d.DocumentId, "Filed", "reconcile",
                            Details: Json(recoveredFiled),
                            Projection: new ProjectionOverrides(FileName: rec.Spec.FileName)), ct);
                        if (rec.Reviewer is null)
                            await StampAutofiledAsync(d.DocumentId, ct); // recovered auto-file: same AUTOFILED stamp as the inline path
                        else
                            // Recovered review filing: land the label + field outcomes/adjudications the inline path
                            // would have — this block previously wrote no corpus data at all, so every crash-recovered
                            // review was a silently lost label. Runs BEFORE the purge below, while the sidecar may
                            // still exist for a full replay. Fail-open inside.
                            await ReviewCapture.RecordRecoveredReviewAsync(transcripts, health, log,
                                d.DocumentId, driveItemId, rec, pdfPath, DateTimeOffset.UtcNow, ct);
                        DeleteFilingSidecar(pdfPath);
                        await PurgeFiledInputsAsync(sink, d.DocumentId, pdfPath, driveItemId, rec.Spec.Target, log, ct); // delete-on-verified (by id, in the recovered spec's target drive)
                        recovered++;
                    }
                    catch (Exception ex)
                    {
                        // Transient (Graph down/throttled, after the resilience handler's own retries) → leave as-is; the
                        // next sweep re-drives. Never dead-letter and never re-file at a different path here. COUNTED
                        // (D-2): this catch retries by design, so a document failing the same way every tick — a
                        // rejected required column, a permanently-invalid spec — used to produce ~720 WARNs a day
                        // while reconcileFailuresSinceStart read zero and no alert existed anywhere.
                        sweepHealth?.MarkReconcileFailure(DateTimeOffset.UtcNow);
                        log.LogWarning("Reconcile: could not complete in-doubt filing for {DocId}; left in {State} for retry. stage={Stage} reconcileAction={ReconcileAction} sweepKind={SweepKind} specContentType={SpecContentType} fieldCount={FieldCount} patientAttached={PatientAttached} {ExType} [{ExChain}] httpStatus={HttpStatus} graphReqId={GraphRequestId}",
                            d.DocumentId, cur.State, "reconcile", "recover_sidecar", filingOnly ? "periodic" : "startup", rec.Spec.ContentType, rec.Spec.Fields.Count, rec.PatientLookupId is not null, ex.GetType().Name, Redact.Chain(ex), ex.Data["httpStatus"], ex.Data["graphRequestId"]);
                    }
                }
                // No sidecar: a "Filing" doc whose intent is lost must be fully reprocessed (any sweep); a stranded live-
                // pipeline doc is re-enqueued ONLY on the startup sweep and only if its state is unchanged since the
                // snapshot — the periodic Filing-only sweep never re-drives in-flight pipeline work.
                else if (cur.State == "Filing" || (!filingOnly && cur.State == d.State))
                {
                    if (await ReEnqueueAsync(d)) reEnqueued++;
                }
            }
            // Supersede recovery — KEYED ON THE .supersede.json SIDECAR (mirror of the filing-sidecar pass above). A crash
            // between the new doc's Filed event and the OLD doc's terminal Superseded event leaves the intent sidecar on
            // disk; DriveSupersedeAsync re-drives it idempotently (delete-by-id 404=success + STATE-GUARDED terminal
            // append). Runs on BOTH startup and periodic sweeps (a supersede interrupted in normal operation must recover
            // without a restart). Claim per new-doc id first so we never race a live review-submit mid-write.
            if (Directory.Exists(inboxDir))
            {
                const string supersedeSuffix = ".supersede.json";
                foreach (var sidecar in Directory.EnumerateFiles(inboxDir, "*" + supersedeSuffix))
                {
                    var name = Path.GetFileName(sidecar);
                    if (name.Length <= supersedeSuffix.Length) continue;
                    var newDocId = name[..^supersedeSuffix.Length];
                    using var claim = claims.Acquire("doc:" + newDocId);
                    if (!claim.Acquired) continue;
                    var newPdfPath = Path.GetFullPath(Path.Combine(inboxDir, $"{newDocId}.pdf"));
                    var intent = ReadSupersedeSidecar(newPdfPath);
                    if (intent is null) continue; // absent/corrupt → NEVER delete on a bad intent; leave the file for inspection
                    // Claim the PREDECESSOR too (B-2, mirroring the submit): the re-drive mutates the OLD document
                    // (deletes its item, appends its terminal event) while /api/reprocess claims doc:{old} for its
                    // whole window. Contention → skip this tick; the sidecar stays for the next sweep.
                    using var oldClaim = claims.Acquire("doc:" + intent.OldDocId);
                    if (!oldClaim.Acquired) continue; // a live path owns the predecessor — not this sweep's to touch
                    // Counts GENUINE completions only — a tripwire refusal must not read as recovered work (same
                    // discipline as the reclassify pass below).
                    try { if (await DriveSupersedeAsync(sink, audit, newPdfPath, intent, log, ct)) supersededRecovered++; }
                    catch (Exception ex) { sweepHealth?.MarkReconcileFailure(DateTimeOffset.UtcNow); log.LogWarning("Reconcile: supersede re-drive for {NewDocId} (old {OldDocId}) failed; sidecar kept for next sweep. {ExType} [{ExChain}]", newDocId, intent.OldDocId, ex.GetType().Name, Redact.Chain(ex)); }
                }
            }

            // Reclassify recovery — KEYED ON THE .reclassify.json SIDECAR (mirror of the supersede pass). Covers BOTH a
            // crash between the write-ahead "Received" and the Graph delete, AND a delete that simply failed inline
            // (transient Graph error) — the endpoint swallows that and leaves the intent here on purpose. The DELETE
            // is free to repeat (404 folds to success); the ANNOTATION is not, so DriveReclassifyAsync makes it
            // at-most-once per intent — this pass runs on every periodic sweep, not just at startup, so an intent that
            // cannot be cleared would otherwise append to the chain every couple of minutes forever. Claim per doc id
            // first so we never race the endpoint or the worker mid-write.
            if (Directory.Exists(inboxDir))
            {
                const string reclassifySuffix = ".reclassify.json";
                foreach (var sidecar in Directory.EnumerateFiles(inboxDir, "*" + reclassifySuffix))
                {
                    var name = Path.GetFileName(sidecar);
                    if (name.Length <= reclassifySuffix.Length) continue;
                    var docId = name[..^reclassifySuffix.Length];
                    using var claim = claims.Acquire("doc:" + docId);
                    if (!claim.Acquired) continue;
                    var docPdfPath = Path.GetFullPath(Path.Combine(inboxDir, $"{docId}.pdf"));
                    var intent = ReadReclassifySidecar(docPdfPath);
                    if (intent is null) continue; // absent/corrupt → NEVER delete on a bad intent; leave it for inspection
                    // Counts GENUINE completions only — a safety-guard refusal must not read as recovered work.
                    try { if (await DriveReclassifyAsync(sink, store, docPdfPath, intent, log, ct)) reclassifyRecovered++; }
                    catch (Exception ex) { sweepHealth?.MarkReconcileFailure(DateTimeOffset.UtcNow); log.LogWarning("Reconcile: reclassify delete re-drive for {DocId} failed; sidecar kept for next sweep. {ExType} [{ExChain}]", docId, ex.GetType().Name, Redact.Chain(ex)); }
                }
            }

            // Rotate recovery — KEYED ON THE .rotate.json SIDECAR (mirror of the supersede pass). A crash between the
            // /Rotate write-back and the terminal-style "Rotated" append leaves the intent on disk; DriveRotateAsync
            // re-drives it idempotently (absolute /Rotate + STATE-GUARDED append), so a reviewer-initiated byte change is
            // ALWAYS audited even if the reviewer never retries. Claim per doc id first to never race a live submit.
            int rotationsRecovered = 0;
            if (Directory.Exists(inboxDir))
            {
                const string rotateSuffix = ".rotate.json";
                foreach (var sidecar in Directory.EnumerateFiles(inboxDir, "*" + rotateSuffix))
                {
                    var name = Path.GetFileName(sidecar);
                    if (name.Length <= rotateSuffix.Length) continue;
                    var rotDocId = name[..^rotateSuffix.Length];
                    using var claim = claims.Acquire("doc:" + rotDocId);
                    if (!claim.Acquired) continue;
                    var rotPdfPath = Path.GetFullPath(Path.Combine(inboxDir, $"{rotDocId}.pdf"));
                    var intent = ReadRotateSidecar(rotPdfPath);
                    if (intent is null) continue; // absent/corrupt → NEVER mutate on a bad intent; leave for inspection
                    if (!File.Exists(rotPdfPath)) { DeleteRotateSidecar(rotPdfPath); continue; } // PDF gone (filed+purged) → drop the orphan marker
                    try { await DriveRotateAsync(audit, rotPdfPath, intent, log, ct); rotationsRecovered++; }
                    catch (Exception ex) { sweepHealth?.MarkReconcileFailure(DateTimeOffset.UtcNow); log.LogWarning("Reconcile: rotate re-drive for {DocId} failed; sidecar kept for next sweep. {ExType} [{ExChain}]", rotDocId, ex.GetType().Name, Redact.Chain(ex)); }
                }
            }

            // Split recovery — KEYED ON THE .split.json SIDECAR (the fifth member of the outbox family). The Segmented
            // append is split's COMMIT and lands BEFORE the child fan-out, so a crash mid-fan-out leaves a terminal
            // parent whose children are re-derivable from the recorded ranges — this pass derives any that are
            // missing, records and enqueues them idempotently, then drops the intent. An intent whose parent has NO
            // Segmented event is an aborted commit (the endpoint surfaced the error; nothing durable exists) and is
            // dropped. Claim per parent id so we never race the endpoint mid-fan-out.
            int splitsRecovered = 0;
            if (Directory.Exists(inboxDir))
            {
                const string splitSuffix = ".split.json";
                foreach (var sidecar in Directory.EnumerateFiles(inboxDir, "*" + splitSuffix))
                {
                    var name = Path.GetFileName(sidecar);
                    if (name.Length <= splitSuffix.Length) continue;
                    var parentDocId = name[..^splitSuffix.Length];
                    using var claim = claims.Acquire("doc:" + parentDocId);
                    if (!claim.Acquired) continue;
                    var parentPdfPath = Path.GetFullPath(Path.Combine(inboxDir, $"{parentDocId}.pdf"));
                    var intent = ReadSplitSidecar(parentPdfPath);
                    if (intent is null) continue; // absent/corrupt → NEVER derive from a bad intent; leave it for inspection
                    try { if (await DriveSplitRecoveryAsync(inboxDir, parentPdfPath, intent, ct)) splitsRecovered++; }
                    catch (Exception ex) { sweepHealth?.MarkReconcileFailure(DateTimeOffset.UtcNow); log.LogWarning("Reconcile: split re-drive for {DocId} failed; sidecar kept for next sweep. {ExType} [{ExChain}]", parentDocId, ex.GetType().Name, Redact.Chain(ex)); }
                }
            }

            if (recovered > 0 || reEnqueued > 0 || supersededRecovered > 0 || rotationsRecovered > 0 || reclassifyRecovered > 0 || splitsRecovered > 0)
                log.LogInformation("Reconcile: recovered {Recovered} in-doubt filing(s), completed {Superseded} pending supersede-delete(s), {ReclassifyRecovered} pending reclassify-delete(s), {RotationsRecovered} pending rotation(s), {SplitsRecovered} pending split fan-out(s), re-enqueued {ReEnqueued} stranded doc(s). sweepKind={SweepKind}", recovered, supersededRecovered, reclassifyRecovered, rotationsRecovered, splitsRecovered, reEnqueued, filingOnly ? "periodic" : "startup");

            async Task<bool> ReEnqueueAsync(DocumentAudit doc)
            {
                var path = Path.GetFullPath(Path.Combine(inboxDir, $"{doc.DocumentId}.pdf"));
                if (!File.Exists(path)) return false;
                // REDRIVE BOUND: a managed processing failure dead-letters in ConsumeAsync's catch — but a failure that
                // KILLS the process (native crash in the PDF renderer, OOM kill) never reaches any catch. The document
                // stays non-terminal, so every sweep would re-drive it into the same crash, forever — the one unbounded
                // retry loop this pipeline had. What counts toward the limit is ATTEMPTS (durable pickup marks in the
                // capture store) since the last real ingest — NOT re-drives: the in-memory queue means every routine
                // restart re-drives the whole unprocessed backlog, and a document that was never picked up must never
                // be parked as a crasher. At the limit, park as DeadLetter — same terminal state, same dashboard retry
                // button as every other failure; the retry's own Received (different actor) resets both counts' anchor.
                // Fail-open everywhere: a failed read (or an absent capture store) re-drives as before.
                int redrives = 0, attempts = 0;
                try
                {
                    var (r, lastIngestAt) = RedriveWindow(await audit.GetEventsAsync(doc.DocumentId, ct));
                    redrives = r;
                    if (transcripts is not null)
                        attempts = await transcripts.AttemptStartsSinceAsync(doc.DocumentId, lastIngestAt, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("Redrive-bound check failed for {DocId} ({ExType}); re-driving without it (fail-open).",
                        doc.DocumentId, ex.GetType().Name);
                }
                if (MaxFailedAttempts > 0 && attempts >= MaxFailedAttempts)
                {
                    try
                    {
                        await audit.AppendAsync(new AuditEntry(doc.DocumentId, "DeadLetter", "reconcile",
                            Details: Json(new
                            {
                                errorClass = ReasonCodes.RedriveLimit, // C6: class + code in the chain; the explanation is projection-only
                                reasonCodes = new[] { ReasonCodes.RedriveLimit },
                            }),
                            Projection: new ProjectionOverrides(Reasons: $"Automatisches Versuchs-Limit erreicht: {attempts} Verarbeitungsversuche seit dem letzten Eingang wurden begonnen und keiner abgeschlossen — die Verarbeitung stürzt an diesem Dokument vermutlich ab. Manuelles Neu-Starten beginnt einen frischen Versuch.")), ct);
                        log.LogError("Redrive bound: parked {DocId} as DeadLetter after {Attempts} started-but-never-completed attempts ({Redrives} re-drives) since its last real ingest; manual retry resets the budget.",
                            doc.DocumentId, attempts, redrives);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Park append failed: the doc stays non-terminal and AT the limit, so the park is re-attempted on
                        // the next startup sweep (or next periodic sweep while the doc is in "Filing" — other states are
                        // only re-driven at startup). Skipping the enqueue is the protection; the event is the record.
                        log.LogError("Redrive bound: DeadLetter append failed for {DocId} ({ExType}); left non-terminal, park re-attempted on the next eligible sweep.",
                            doc.DocumentId, ex.GetType().Name);
                    }
                    return false;
                }
                await audit.AppendAsync(new AuditEntry(doc.DocumentId, "Received", "reconcile",
                    Details: Json(new { channel = "Reconcile" }), Projection: new ProjectionOverrides(Reasons: "")), ct);
                await queue.Writer.WriteAsync(new UploadJob(doc.DocumentId, path, doc.OriginalName ?? doc.DocumentId, Origin: JobOrigin.ReconcileRedrive), ct);
                return true;
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            // Counted as well as logged: the tick still completes after this (the purges run, the timer survives),
            // so a persistently failing reconcile would otherwise look healthy on the tick counter alone.
            sweepHealth?.MarkReconcileFailure(DateTimeOffset.UtcNow);
            log.LogError("Reconciliation sweep failed; stranded documents stay visible in the dashboard and are retried next sweep. sweepKind={SweepKind} {ExType} [{ExChain}]", filingOnly ? "periodic" : "startup", ex.GetType().Name, Redact.Chain(ex));
        }
    }

    /// <summary>The redrive window since the last real ingest: the count of "Received" events appended by the
    /// reconcile sweep (actor "reconcile") after the most recent "Received" from any OTHER actor — original upload,
    /// drain, split child, human retry/reprocess — plus that anchor event's timestamp (attempt marks are counted
    /// against it). Progress events between re-drives deliberately do NOT reset the count (a document that
    /// transcribes and then kills the process every time is still poison); only a fresh ingest does — so the
    /// dashboard's manual retry hands the document a fresh budget by construction.</summary>
    internal static (int Redrives, DateTimeOffset LastIngestAt) RedriveWindow(IReadOnlyList<AuditEvent> events)
    {
        var n = 0;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Type != "Received") continue;
            if (events[i].Actor != "reconcile") return (n, events[i].At);
            n++;
        }
        return (n, DateTimeOffset.MinValue);
    }

    /// <summary>See <see cref="RedriveWindow"/> — the re-drive count alone.</summary>
    internal static int RedrivesSinceLastIngest(IReadOnlyList<AuditEvent> events) => RedriveWindow(events).Redrives;

    /// <summary>How many started-but-never-completed PROCESSING ATTEMPTS a document gets before reconcile parks it
    /// as DeadLetter instead of re-driving again (ADELAIDE_REDRIVE_LIMIT, default 5; 0 disables the bound, matching
    /// the sibling gates' 0-means-off convention; negative/invalid values fall back to the default; read once at
    /// startup so the bound cannot flip mid-run). Attempts are durable pickup marks in the capture store — mere
    /// re-drives never count, so a backlog document queued through any number of routine restarts is unbounded by
    /// design, while a worker-killing document parks after the fifth pickup that leaves no completed run.</summary>
    internal static readonly int MaxFailedAttempts =
        int.TryParse(Environment.GetEnvironmentVariable("ADELAIDE_REDRIVE_LIMIT"), out var attemptLimit) && attemptLimit >= 0 ? attemptLimit : 5;

    internal async Task ProcessAsync(UploadJob job, CancellationToken ct)
    {
        // Every log line emitted while processing this document carries DocId via this scope, so an incident is traceable
        // end-to-end with one filter (DocId=<id>) across all stages — the single most useful affordance for a later agent.
        using var logScope = log.BeginScope(new Dictionary<string, object> { ["DocId"] = job.DocId, ["origin"] = job.Origin.ToString() });
        log.LogInformation("Processing started for {DocId} (forceReview={ForceReview}) stage={Stage}", job.DocId, job.ForceReview, "start");

        // Idempotency: never re-file a document already Filed (a stale/duplicate enqueue, or a retry of a finished doc).
        // ForceReview (reclassify) deliberately re-runs even a filed doc and routes it back to review for confirmation.
        if (!job.ForceReview && (await audit.GetDocumentAsync(job.DocId, ct))?.State == "Filed")
        {
            log.LogInformation("Skipping {DocId}: already Filed. stage={Stage} state={State}", job.DocId, "dedup", "Filed");
            return;
        }

        using var scope = scopes.CreateScope();
        var s = scope.ServiceProvider;
        var transcriber = s.GetRequiredService<ITranscriber>();
        var extractor = s.GetRequiredService<IExtractor>();
        var directory = s.GetRequiredService<IPatientDirectory>();
        var sink = s.GetRequiredService<IFiler>(); // same SharePointSink at runtime (DI); IFiler is the test seam

        // B1: the stage about to run + the last good state; stamped onto any UNCAUGHT exception (ex.Data) below so the
        // dead-letter log in ConsumeAsync — outside this scope — can place the failure in the document's journey.
        var stage = "transcribe";
        var lastState = "Received";
        try
        {
            var doc = new IncomingDocument(job.DocId, job.Path, job.Channel, job.OriginalName, job.SourceRecordId);
            Transcript t;
            // A split/segment child carries ITS slice of the parent transcript → reuse it and SKIP the (expensive) vision
            // call; re-decode barcodes LOCALLY on this child's pages so its KVNR identity is its own, never a sibling's.
            // The reuse is audited as its own Transcribed source ("reuse-split") — no silent inheritance. Absent (or a lost
            // sidecar) ⇒ transcribe fresh, the safe fallback.
            if (ReadPreTranscript(job.Path) is { } carried)
            {
                var reusedBarcodes = ScanBarcodesLocal(job.Path);
                t = new Transcript(string.Join("\n\n", carried.PageMarkdown), carried.PageMarkdown.Count, carried.Legibility, reusedBarcodes, carried.PageMarkdown);
                await audit.AppendAsync(new AuditEntry(job.DocId, "Transcribed", "reuse-split",
                    Details: Json(TranscribedEventDetails.ReuseSplit(t, carried.ParentDocId, carried.SeparatorVersion))), ct);
                DeletePreTranscript(job.Path); // consumed
                log.LogInformation("Reused parent transcript for split child {DocId} (parent {Parent}, {Pages}p, {Barcodes} barcode(s)) — skipped vision transcription. stage={Stage}", job.DocId, carried.ParentDocId, t.PageCount, t.Barcodes.Count, "transcribe");
            }
            else
            {
                t = await transcriber.TranscribeAsync(doc, ct);
                // Model + prompt provenance (safe/structural, no PHI). `model` is the deployment we asked for;
                // `modelVersion` is what the API says actually ran (a deployment can be repointed at a new version with
                // no code change, so only this answers "which model version produced this transcript"); `promptHash`
                // identifies the prompt revision. Without all three, a post-migration change in legibility scores or
                // transcript quality cannot be attributed to the model rather than a prompt edit.
                // Carries the transcript's SHA-256, NOT the transcript: the chain proves WHICH text was produced
                // without containing it, so a retained copy stays verifiable while erasure stays possible. Shape is
                // pinned by TranscribedEventShapeTests against this same builder. See TranscribedEventDetails.
                await audit.AppendAsync(new AuditEntry(job.DocId, "Transcribed", "vision",
                    Details: Json(TranscribedEventDetails.Vision(t))), ct);
            }
            lastState = "Transcribed"; stage = "segment";

            // SEGMENT — reconcile the physical page-stream into 1..N logical units. Foundation: one unit = the whole file
            // (N=1 zero-churn; UnitId = fileDocId). The barcode-cardinality guard replaces this to return N>1 for barcoded
            // stacks (and route unreadable cases to review). Identity is the DerivationKey (replay-stable), never the
            // ordinal (build-auth D1).
            var fileSha = (await store.GetDocumentAsync(job.DocId, ct))?.Sha256 ?? job.DocId; // content hash from ingest
            var units = SegmentFile(job.DocId, t, job.Path, fileSha);
            // The Segmented event is the SOLE tamper-evident file→units link: the fan-out (unit list + ranges + keys +
            // separatorVersion) lives INSIDE the hash chain, so no unit can be added/dropped/re-ranged without breaking it.
            await audit.AppendAsync(new AuditEntry(job.DocId, "Segmented", "segmenter", Details: Json(new
            {
                unitCount = units.Count,
                separatorVersion = SeparatorVersion,
                units = units.Select(u => new { unitId = u.UnitId, range = u.Provenance!.PageRange.ToString(), derivationKey = u.DerivationKey, evidence = u.Provenance!.BoundaryEvidence }).ToArray(),
            })), ct);
            lastState = "Segmented"; stage = "extract";

            // Foundation processes the single unit inline (N=1); the barcode-cardinality guard will loop
            // `foreach (var unit in units)` over N units here, re-keying each unit's Extracted…Filed events to unit.UnitId.
            var unit = units[0];
            var data = await extractor.ExtractAsync(unit, ct);
            var contentType = data.Value("ContentType") ?? DocumentCatalog.Fallback;
            // Actor was the hardcoded literal "gpt-4o" — it did NOT read the configured deployment, so every row claimed
            // gpt-4o no matter which model ran, writing a false provenance claim into an append-only hash-chained ledger.
            // It now reports the deployment that actually served the extraction.
            await audit.AppendAsync(new AuditEntry(job.DocId, "Extracted", data.ModelDeployment ?? "extractor", Details: Json(new
            {
                contentType,
                classifySource = data.Get("ContentType")?.Source,
                originalContentType = data.Value("OriginalContentType"), // set when the self-check overruled the classifier
                seed = data.ModelSeed,                  // reproducibility provenance (safe/structural, no PHI):
                systemFingerprint = data.ModelFingerprint, // which backend produced this — the tripwire for "behaviour changed, code didn't"
                model = data.ModelDeployment,           // deployment asked for
                modelVersion = data.ModelServed,        // model id the API says it ran (name + version)
                promptHash = data.PromptHash,           // prompt revision — separates a model change from a prompt change
                reasoningEffort = data.ReasoningEffort, // behavioural setting actually SENT; null on a non-reasoning model
            })), ct);
            // A10: required-field PRESENCE map — {fieldName: bool}, NEVER {fieldName: value}. The column NAMES + presence
            // bools are structural; the values are PHI. Lets a downstream gate/file failure read as "Dokumentendatum absent".
            var typeDef = DocumentCatalog.Find(contentType);
            var fieldPresence = typeDef is null
                ? new Dictionary<string, bool>()
                : typeDef.Fields.ToDictionary(f => f.Column, f => !string.IsNullOrWhiteSpace(data.Value(f.Column)));
            log.LogInformation("Extracted {DocId}: contentType={ContentType} via {ClassifySource} fingerprint={Fingerprint} stage={Stage} state={State} fieldCount={FieldCount} present={@FieldPresence}",
                job.DocId, contentType, data.Get("ContentType")?.Source ?? "?", data.ModelFingerprint ?? "(none)", "extract", "Extracted", data.Fields.Count, fieldPresence);
            lastState = "Extracted"; stage = "match";

            var match = await directory.MatchAsync(data, ct);
            await audit.AppendAsync(new AuditEntry(job.DocId, "Matched", "graph", Details: Json(new { outcome = match.Outcome.ToString(), patientListItemId = match.Patient?.ListItemId })), ct);
            lastState = "Matched"; stage = "gate";

            var result = IntakePipeline.Decide(unit, t, data, match, new PipelineOptions());
            if (job.ForceReview && result.Disposition == Disposition.AutoFile)
                result = result with // reclassify flow: the human confirms before anything is re-filed
                {
                    Disposition = Disposition.Review,
                    Reasons = new[] { "zur Korrektur erneut verarbeitet — bitte prüfen und bestätigen" },
                    ReasonCodes = new[] { "reclassify_review" },
                };

            // CAPTURE — unconditional (docs/CAPTURE.md): every processed document lands its transcript, model-input
            // artifacts and corpus row; the old ShouldRetain selection (gate-hit + sample) is retired — write-time
            // selection is how the corpus starved while the pipeline ran. Still AFTER the gate, because the corpus
            // row records the gate outcome. Writes to the SEPARATE capture store, never to the ledger (the ledger
            // already carries only the SHA-256). Purely additive and fail-open-but-counted: a capture failure must
            // never fail a document that is otherwise fine.
            if (transcripts is not null)
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    var txSha = TranscriptDigest.Of(t.Markdown);
                    var contentSha = (await store.GetDocumentAsync(job.DocId, ct))?.Sha256;   // the PDF's content hash
                    await transcripts.PutAsync(job.DocId, t.Markdown, txSha, now, ct);

                    // Model INPUTS, previously discarded — page slices, decoded barcode payloads, checkbox evidence.
                    // PHI, so they sit beside the transcript and are erased with it.
                    await transcripts.PutArtifactsAsync(job.DocId,
                        t.PageMarkdown is { Count: > 0 } pm ? Json(pm) : null,
                        t.Barcodes.Count > 0 ? Json(t.Barcodes.Select(b => new { symbology = b.Symbology, rawText = b.RawText, checksumValid = b.ChecksumValid })) : null,
                        data.CheckboxEvidence, now, ct);

                    // The measurement row. NON-PHI: labels, provenance, scores and references only. The human's Label
                    // arrives later at review; `Label IS NOT NULL` is what makes a row scoreable.
                    await transcripts.PutCorpusAsync(new Adelaide.Audit.CorpusRow(
                        DocumentId: job.DocId, At: now, TranscriptSha256: txSha,
                        Model: t.ModelDeployment, ModelVersion: t.ModelServed, PromptHash: t.PromptHash,
                        // What was actually SENT, computed by ModelCapabilities so the stamp cannot disagree with the
                        // wire. Null on a non-reasoning model — the honest value, not a guessed "low".
                        ReasoningEffort: t.ReasoningEffort, SystemFingerprint: t.ModelFingerprint,
                        Pages: t.PageCount, Legibility: t.QualityScore, Barcodes: t.Barcodes.Count,
                        TranscriptChars: t.Markdown.Length,
                        ModelDomain: data.Value("Domain"), ModelContentType: data.Value("ContentType"),
                        ModelConfidence: data.Get("ContentType")?.Confidence,
                        ContentSha256: contentSha,
                        ReviewState: result.Disposition.ToString(),
                        ReasonCodesJson: result.ReasonCodes is { Count: > 0 } rc ? Json(rc) : null), ct);

                    // Per-field measurement rows: name + proposed? + the confidence the pipeline recorded + source,
                    // one per extracted field. Outcome stays NULL here; review or the auto-file stamp fills it. This
                    // is where per-field confidence STOPS dying with the sidecar (INVESTIGATION.md §3.2).
                    // Names are gated at the extractor (FieldVocabulary), so they are catalog columns by the time
                    // they arrive. The length bound is kept as the substrate guard the sibling write already has
                    // (ReviewCapture.ComputeFieldRecords): FieldName is NVARCHAR(128), and an over-long name would
                    // throw here and silently drop the WHOLE document's corpus capture on Azure SQL while SQLite
                    // stored it — a divergence, not a clean failure.
                    await transcripts.PutCorpusFieldsAsync(job.DocId,
                        data.Fields.Where(f => f.Name.Length <= 128).Select(f => new Adelaide.Audit.CorpusFieldRow(
                            f.Name, !string.IsNullOrWhiteSpace(f.Value), f.Confidence, f.Source)), now, ct);
                }
                catch (Exception ex)
                {
                    health?.CountFailure("corpus");
                    log.LogWarning("Corpus capture failed for {DocId} ({ExType}); processing continues. The ledger's transcriptSha256 is unaffected.", job.DocId, ex.GetType().Name);
                }
            }

            // The document is already claimed for this whole ProcessAsync (see ConsumeAsync), so no other path can file it.
            // Layer-2 duplicate check (business key): a document with the same patient KVNR, content type and date is the
            // same document in business terms even if the bytes differ (re-scan, fax then post). CLAIM the business key too
            // (non-blocking): if another DIFFERENT document with that key is being filed right now, or one is already filed,
            // this one is a probable duplicate → review. Never auto-reject — the reviewer compares side-by-side and decides.
            // Two duplicate layers, each guarded against a concurrent racer by a non-blocking claim:
            //  (1) EXACT BYTES — same content hash as an already-filed doc. Independent of extraction, so it catches re-sends
            //      AND the case the business key misses when no clean date/KVNR was read (where dupKey is null below).
            //  (2) BUSINESS KEY — same patient KVNR + type + date even if the BYTES differ (re-scan, fax then post).
            // Either hit → review (never auto-reject; the reviewer compares side-by-side). The claims serialise concurrent
            // workers so two identical docs in the same processing window can't both pass the "already filed?" check.
            var sha = (await store.GetDocumentAsync(job.DocId, ct))?.Sha256;
            var dupKey = SharePointSink.BuildDuplicateKeyPrefix(result, contentType);
            // Layer 3 (BUSINESS content fingerprint): a non-patient document has no patient+type+date key, so a re-scan
            // (same text, different bytes) slips past Layers 1-2. Fingerprint the transcript — the SAME LeadWith(t) text the
            // review sidecar stores and a business filing records on its Filed event — so the value the reviewer files under
            // matches what the next document fingerprints. ONLY for the no-KVNR (business-destined) documents (dupKey null).
            var bizKey = dupKey is null ? BusinessDedup.Fingerprint(BarcodeSummary.LeadWith(t.Markdown, t.Barcodes)) : null;
            using var shaClaim = sha is not null ? claims.Acquire("sha:" + sha) : default;
            using var keyClaim = dupKey is not null ? claims.Acquire("key:" + dupKey) : default;
            using var bizClaim = bizKey is not null ? claims.Acquire("biz:" + bizKey) : default;
            DocumentAudit? duplicate = null;
            string? dupReason =
                (sha is not null && !shaClaim.Acquired) || (dupKey is not null && !keyClaim.Acquired) || (bizKey is not null && !bizClaim.Acquired)
                    ? "mögliches Duplikat — ein inhaltsgleiches Dokument wird gerade abgelegt"
                : sha is not null && (duplicate = await store.FindFiledBySha256Async(sha, job.DocId, ct)) is not null
                    ? $"inhaltsgleich (SHA-256) mit bereits abgelegtem „{duplicate.OriginalName ?? duplicate.DocumentId}“"
                : dupKey is not null && (duplicate = await store.FindFiledByFileNamePrefixAsync(dupKey, job.DocId, ct)) is not null
                    ? $"mögliches Duplikat von „{duplicate.OriginalName ?? duplicate.DocumentId}“ (gleicher Patient, Typ und Datum)"
                : bizKey is not null && (duplicate = await FindFiledBusinessDuplicateAsync(bizKey, job.DocId, ct)) is not null
                    ? $"mögliches Duplikat von „{duplicate.OriginalName ?? duplicate.DocumentId}“ (inhaltsgleiches Geschäftsdokument)"
                : null;
            if (dupReason is not null)
                // B2 PHI: the dupReason PROSE interpolates the original filename (KVNR); the CODE is the bare tag — NO filename.
                result = result with { Disposition = Disposition.Review, Reasons = result.Reasons.Append(dupReason).ToArray(),
                    ReasonCodes = (result.ReasonCodes ?? Array.Empty<string>()).Append("duplicate_suspect").ToArray() };

            if (result.Disposition == Disposition.AutoFile)
            {
                stage = "file";
                var spec = SharePointSink.BuildSpec(doc, unit, result);
                var patientId = match.Patient?.ListItemId;
                // Outbox: the deletable sidecar holds the full payload (filename, content type, fields, AND the transcript for
                // Volltext) so recovery can COMPLETE the filing from intent without re-extraction; the immutable audit records
                // only the FACT + filename (no transcript in the un-erasable hash chain). Sidecar FIRST, then the atomic
                // "Filing" event — so any doc that ever reaches state "Filing" is guaranteed to have its sidecar on disk.
                await WriteFilingSidecarAsync(job.Path, spec, patientId, ct);
                await audit.AppendAsync(new AuditEntry(job.DocId, "Filing", "worker", Details: Json(new { contentType = spec.ContentType, patientListItemId = patientId }),
                    Projection: new ProjectionOverrides(FileName: spec.FileName)), ct);
                FilingResult filing;
                try { filing = await sink.FileAsync(spec, ct); }
                catch (Exception ex)
                {
                    // Outbox invariant: a doc that reached "Filing" is COMPLETED FROM INTENT by reconcile, never dead-lettered
                    // with half-provisioned bytes in SharePoint. Catch here so the failure does NOT bubble to ConsumeAsync's
                    // catch (which appends "DeadLetter" — excluded from reconcile, so it would never be re-driven). Leave the
                    // doc in "Filing" with its sidecar intact; the periodic sweep re-drives it idempotently to the same path.
                    if (ct.IsCancellationRequested) log.LogInformation("Auto-file interrupted by shutdown for {DocId}; left in Filing. stage={Stage} state={State}", job.DocId, "file", "Filing");
                    else log.LogWarning("Auto-file failed for {DocId}; left in Filing for reconcile to complete. stage={Stage} state={State} specContentType={SpecContentType} fieldCount={FieldCount} patientAttached={PatientAttached} {ExType} [{ExChain}] httpStatus={HttpStatus} graphReqId={GraphRequestId}",
                        job.DocId, "file", "Filing", spec.ContentType, spec.Fields.Count, patientId is not null, ex.GetType().Name, Redact.Chain(ex), ex.Data["httpStatus"], ex.Data["graphRequestId"]);
                    return;
                }
                var driveItemId = filing.DriveItemId;
                try
                {
                    // The bytes + metadata are already live in SharePoint; only RECORDING the result remains. If THIS append
                    // fails, leave the doc in "Filing" (do NOT bubble to the DeadLetter catch) so reconcile re-drives the
                    // idempotent spec and records "Filed". Recording, like filing, must never strand an already-filed doc.
                    await audit.AppendAsync(new AuditEntry(job.DocId, "Filed", "sharepoint", Details: Json(new { contentType = spec.ContentType, patientListItemId = patientId, disposition = "AutoFile", driveItemId, pdfaDeclared = filing.PdfADeclared, diRendition = filing.DiRendition }),
                        Projection: new ProjectionOverrides(FileName: spec.FileName)), ct);
                    log.LogInformation("Auto-filed {DocId} as {ContentType} (patient {PatientListItemId}) stage={Stage} state={State} matchMethod={MatchMethod} matchOutcome={MatchOutcome} candidateCount={CandidateCount}",
                        job.DocId, spec.ContentType, patientId, "file", "Filed", match.Method, match.Outcome.ToString(), match.Candidates?.Count ?? 0);
                    await StampAutofiledAsync(job.DocId, ct); // per-field Outcome=AUTOFILED — unlabeled BY DESIGN, never NULL-pending
                    DeleteFilingSidecar(job.Path);
                    await PurgeFiledInputsAsync(sink, job.DocId, job.Path, driveItemId, spec.Target, log, ct); // delete-on-verified: drop the local PDF once SharePoint confirms it (by id, in the spec's target drive)
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) log.LogInformation("Filed-append interrupted by shutdown for {DocId}; left in Filing. stage={Stage} state={State}", job.DocId, "file", "Filing");
                    else log.LogWarning(ex, "Filed-append failed for {DocId} after a successful upload; left in Filing for reconcile. stage={Stage} state={State}", job.DocId, "file", "Filing");
                    return;
                }
            }
            else
            {
                await WriteReviewSidecarAsync(job, contentType, t, data, match, result.Reasons, duplicate, ct);
                // C6: the chain carries reasonCodes (structural, PHI-safe); the German reason PROSE — which is NOT all
                // PHI-free (a duplicate-detection reason interpolates the original filename, which embeds the patient
                // KVNR) — goes to the erasable projection and the review sidecar only.
                await audit.AppendAsync(new AuditEntry(job.DocId, "NeedsReview", "gate", Details: Json(new { disposition = "Review", reasonCodes = result.ReasonCodes ?? Array.Empty<string>(), confidence = result.OverallConfidence, duplicateOf = duplicate?.DocumentId }),
                    Projection: new ProjectionOverrides(Reasons: string.Join("; ", result.Reasons))), ct);
                // reasonCount (not the reason text) in the log line, for the same reason.
                log.LogInformation("Routed {DocId} to review: contentType={ContentType} reasonCount={ReasonCount} reasonCodes={@ReasonCodes} duplicateOf={DuplicateOf} stage={Stage} state={State} matchMethod={MatchMethod} matchOutcome={MatchOutcome} candidateCount={CandidateCount}",
                    job.DocId, contentType, result.Reasons.Count, result.ReasonCodes ?? Array.Empty<string>(), duplicate?.DocumentId ?? "(none)", "gate", "NeedsReview", match.Method, match.Outcome.ToString(), match.Candidates?.Count ?? 0);
            }
        }
        catch (Exception ex)
        {
            ex.Data["stage"] = stage; ex.Data["state"] = lastState; // B1: place the failure in the journey for ConsumeAsync's dead-letter log
            throw;
        }
    }

    /// <summary>Stamp a document's corpus_fields rows Outcome=AUTOFILED at (auto-)filing — the distinct value that
    /// keeps "unlabeled by design" from ever looking like "unlabeled by bug" (or vice versa). Fail-open + counted.</summary>
    private async Task StampAutofiledAsync(string docId, CancellationToken ct)
    {
        if (transcripts is null) return;
        try
        {
            await transcripts.SetFieldOutcomesAsync(docId, Array.Empty<(string, string)>(), "AUTOFILED", DateTimeOffset.UtcNow, ct);
        }
        catch (Exception ex)
        {
            health?.CountFailure("corpus_fields");
            log.LogWarning("AUTOFILED stamp failed for {DocId} ({ExType}); rows stay pending until the next processing.", docId, ex.GetType().Name);
        }
    }

    private const string SeparatorVersion = "seg-v1";

    // Foundation segmenter: one unit = the whole file (N=1 zero-churn, UnitId = fileDocId). The barcode-cardinality guard
    // replaces this to return N>1 for barcoded stacks and route unreadable cases to review. Deterministic + replayable —
    // identity is the DerivationKey, not the ordinal (build-auth D1), so a replayed job reuses the same UnitId.
    private static IReadOnlyList<DocumentUnit> SegmentFile(string fileDocId, Transcript t, string filePath, string fileSha)
    {
        var range = new PageRange(1, Math.Max(1, t.PageCount));
        var unitId = UnitKeys.UnitId(fileDocId, 1, 1); // == fileDocId for N=1
        var provenance = new UnitProvenance(fileSha, range, SeparatorVersion, "policy-v1", new[] { "whole-file" });
        return new[] { new DocumentUnit(unitId, range.First, range.Last, t, filePath, fileSha,
            UnitKeys.DerivationKey(fileSha, range, SeparatorVersion), Provenance: provenance) };
    }

    // Layer-3 business dedup lookup: the most recent FILED document whose Filed event carries this content fingerprint
    // (written ONLY on a business filing). Matched by the fingerprint substring — a SHA-256 hex is unique, so no cross-
    // match with unrelated Details; excludes the current doc and requires the match still be Filed (defensive: the key is
    // only ever written on a Filed event, so any hit is already a filed business document).
    private async Task<DocumentAudit?> FindFiledBusinessDuplicateAsync(string fingerprint, string excludeDocId, CancellationToken ct)
    {
        var id = await store.FindDocumentIdByDetailAsync(fingerprint, ct);
        if (id is null || string.Equals(id, excludeDocId, StringComparison.Ordinal)) return null;
        var d = await store.GetDocumentAsync(id, ct);
        return d is { State: "Filed" } ? d : null;
    }

    // The review pre-fill payload: the model's full extraction + the match candidate + gate reasons, stored next
    // to the captured PDF so the review screen can pre-populate the form (and later diff the human's corrections).
    private static async Task WriteReviewSidecarAsync(UploadJob job, string contentType, Transcript transcript, ExtractedData data, PatientMatch match, IReadOnlyList<string> reasons, DocumentAudit? duplicate, CancellationToken ct)
    {
        var payload = new
        {
            documentId = job.DocId,
            original = job.OriginalName,
            contentType,
            channel = job.Channel.ToString(), // source channel — carried so a business review-filing can record it (non-PHI); ignored by the medical path
            duplicateOf = duplicate is null ? null : new { documentId = duplicate.DocumentId, original = duplicate.OriginalName, fileName = duplicate.FileName },
            classifyConfidence = data.Get("ContentType")?.Confidence ?? 0d,
            // The domain classifier's PROPOSAL (medical vs business). Carried so the review screen can pre-tick the
            // business box and — the part that matters — so ReviewDiff can record whether the human CONFIRMED or
            // CORRECTED it. Without the proposal on the sidecar the checkbox stays an unlabelled human decision and
            // domain accuracy is unmeasurable. Null on documents extracted before the domain step existed.
            domain = data.Value("Domain"),
            domainConfidence = data.Get("Domain")?.Confidence,
            markdown = BarcodeSummary.LeadWith(transcript.Markdown, transcript.Barcodes),   // lead with the full decoded barcode; also travels for review-filing's Volltext
            pageCount = transcript.PageCount,
            pageMarkdown = transcript.PageMarkdown, // RAW per-page transcript (not barcode-led) — the slice source for a manual split; null when the transcriber could not page-delimit
            legibility = transcript.QualityScore,   // carried onto split children so a low-legibility parent's parts still route to review (not silently auto-filed)

            fields = data.Fields.Select(f => new { name = f.Name, value = f.Value, confidence = f.Confidence, source = f.Source }),
            match = new
            {
                outcome = match.Outcome.ToString(),
                method = match.Method,
                candidate = match.Patient is { } p ? Candidate(p) : null,
                candidates = match.Candidates?.Select(Candidate), // the collision set on Multiple — pre-loads the picker
            },
            reasons,
        };
        await File.WriteAllTextAsync(Path.ChangeExtension(job.Path, ".review.json"), Json(payload), ct);
    }

    private static object Candidate(PatientRef p) => new
    {
        listItemId = p.ListItemId, kvnr = p.Kvnr, lastName = p.LastName, firstName = p.FirstName,
        birthDate = p.BirthDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), status = p.Status,
        patientNr = p.PatientNr, klientId = p.KlientId,
    };

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    // ---- filing sidecar: the deletable recovery payload (spec + transcript) written next to the captured PDF ----
    internal static string FilingSidecarPath(string pdfPath) => Path.ChangeExtension(pdfPath, ".filing.json");

    /// <summary>Write the filing sidecar (recovery payload) BEFORE the "Filing" audit event, so any doc in state
    /// "Filing" is guaranteed to have its sidecar on disk. Holds the transcript (PHI) — fine on the deletable inbox,
    /// the same store as the captured PDF; deliberately NOT in the immutable audit.</summary>
    internal static async Task WriteFilingSidecarAsync(string pdfPath, FilingSpec spec, int? patientLookupId, CancellationToken ct,
        string? reviewer = null, object? reviewDiff = null, bool acknowledgedDuplicate = false) =>
        await File.WriteAllTextAsync(FilingSidecarPath(pdfPath),
            Json(new { fileName = spec.FileName, contentType = spec.ContentType, fields = spec.Fields, volltext = spec.Volltext, patientLookupId,
                // The DESTINATION (Phase-1 business seam): persisted so reconcile re-drives a recovered filing to the SAME
                // site/library after a crash — a business filing must complete to the business library, not Patient Records.
                // Null/absent → the medical default on read, so existing on-disk sidecars and the medical path are unchanged.
                target = spec.Target is null ? null : (object)new { siteId = spec.Target.SiteId, library = spec.Target.Library, contentType = spec.Target.ContentType, requiresPatient = spec.Target.RequiresPatient },
                // Review-origin filings carry the human's provenance so recovery preserves the "Reviewed" correction diff
                // and the true "ReviewedThenFiled" attribution; null for the worker auto-file path.
                review = reviewer is null ? null : (object)new { reviewer, diff = reviewDiff, acknowledgedDuplicate } }), ct);

    /// <summary>The recovery payload read back from a filing sidecar: the SharePoint spec, plus (for a review-origin
    /// filing) the reviewer + correction diff + duplicate-ack so reconcile can complete it with full audit fidelity.</summary>
    internal sealed record FilingRecovery(FilingSpec Spec, int? PatientLookupId, string? Reviewer, string? ReviewDiffJson, bool AcknowledgedDuplicate);

    /// <summary>Read the sidecar back for crash recovery (no re-extraction). Null if absent/corrupt → the caller falls
    /// back to full re-processing. Field values are normalised to native types so the Graph PATCH body matches the
    /// forward path exactly.</summary>
    internal static FilingRecovery? ReadFilingSidecar(string pdfPath)
    {
        var path = FilingSidecarPath(pdfPath);
        if (!File.Exists(path)) return null;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var r = d.RootElement;
            var fileName = r.GetProperty("fileName").GetString()!;
            var contentType = r.GetProperty("contentType").GetString()!;
            var volltext = r.TryGetProperty("volltext", out var vt) && vt.ValueKind == System.Text.Json.JsonValueKind.String ? vt.GetString()! : "";
            int? patientLookupId = r.TryGetProperty("patientLookupId", out var pid) && pid.ValueKind == System.Text.Json.JsonValueKind.Number ? pid.GetInt32() : null;
            var fields = new Dictionary<string, object?>();
            if (r.TryGetProperty("fields", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var p in f.EnumerateObject()) fields[p.Name] = NativeJson(p.Value);
            string? reviewer = null, reviewDiffJson = null; bool ack = false;
            if (r.TryGetProperty("review", out var rev) && rev.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                reviewer = rev.TryGetProperty("reviewer", out var rv) && rv.ValueKind == System.Text.Json.JsonValueKind.String ? rv.GetString() : null;
                reviewDiffJson = rev.TryGetProperty("diff", out var df) && df.ValueKind != System.Text.Json.JsonValueKind.Null ? df.GetRawText() : null;
                ack = rev.TryGetProperty("acknowledgedDuplicate", out var ad) && ad.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            // The recorded destination (business filings only). Absent/incomplete → null → the sink's medical default, so
            // every pre-existing medical sidecar recovers to Patient Records exactly as before.
            FilingTarget? target = null;
            if (r.TryGetProperty("target", out var tg) && tg.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var siteId = tg.TryGetProperty("siteId", out var si) && si.ValueKind == System.Text.Json.JsonValueKind.String ? si.GetString() : null;
                var library = tg.TryGetProperty("library", out var lb) && lb.ValueKind == System.Text.Json.JsonValueKind.String ? lb.GetString() : null;
                var targetCt = tg.TryGetProperty("contentType", out var tc) && tc.ValueKind == System.Text.Json.JsonValueKind.String ? tc.GetString() : null;
                var reqPat = !tg.TryGetProperty("requiresPatient", out var rp) || rp.ValueKind != System.Text.Json.JsonValueKind.False; // absent → default true
                if (!string.IsNullOrEmpty(siteId) && !string.IsNullOrEmpty(library))
                    target = new FilingTarget(siteId, library, targetCt, reqPat);
            }
            return new FilingRecovery(new FilingSpec(fileName, contentType, fields, volltext, pdfPath, target), patientLookupId, reviewer, reviewDiffJson, ack);
        }
        catch { return null; }
    }

    private static object? NativeJson(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => e.GetString(),
        System.Text.Json.JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => e.GetRawText(),
    };

    internal static void DeleteFilingSidecar(string pdfPath)
    {
        try { File.Delete(FilingSidecarPath(pdfPath)); } catch { /* best effort */ }
    }

    /// <summary>Delete the REVIEW sidecar after filing — only when the evidence-preservation kill-switch allows it
    /// AND no grace window defers to the aged sweep.
    ///
    /// Unlike the filing sidecar, this one carries the TRANSCRIPT, which is the only record of what the model read.
    /// It was previously deleted unconditionally, so <see cref="PurgeGate"/> preserved the PDF while the transcript
    /// went anyway: filings in August 2026 kept 100% of PDFs and 5% of transcripts. The GRACE deferral arrived two
    /// days after that gating and covered only the PDF purge — a sequencing oversight (A4, confirmed 2026-08-16)
    /// that made the window incoherent: armed-with-grace kept the PDF for misfile repair while burning the sidecar
    /// beside it, and the aged sweep's core deletes the sidecar at window end anyway. Now the sidecar rides the
    /// same deferral as the PDF: one repair window, one release, at the sweep. A method rather than an inline
    /// try/catch so the behaviour is testable — the gates read their environment once at startup, so tests inject
    /// the decisions via the explicit overload.</summary>
    internal static void DeleteReviewSidecarIfPurgeEnabled(string sidecarPath, string docId, Microsoft.Extensions.Logging.ILogger log) =>
        DeleteReviewSidecarIfPurgeEnabled(sidecarPath, docId, log, PurgeGate.Enabled, FiledPurgeGrace.Deferred);

    /// <summary>Gate-explicit overload (tests inject the decisions; production resolves them from the statics).</summary>
    internal static void DeleteReviewSidecarIfPurgeEnabled(string sidecarPath, string docId, Microsoft.Extensions.Logging.ILogger log,
        bool purgeEnabled, bool graceDeferred)
    {
        if (!purgeEnabled)
        {
            log.LogInformation("Review sidecar KEPT for {DocId} (ADELAIDE_PURGE_ENABLED not set) — transcript preserved for measurement.", docId);
            return;
        }
        if (graceDeferred)
        {
            log.LogDebug("Review sidecar delete deferred for {DocId}: {Days}d grace window active; the aged sweep releases it with the PDF after the window.", docId, FiledPurgeGrace.Days);
            return;
        }
        try { File.Delete(sidecarPath); }
        catch (Exception ex) { log.LogWarning("Best-effort delete of review sidecar failed for {DocId} (after filing; {Error}); stale sidecar remains on disk.", docId, ex.GetType().Name); }
    }

    // ---- Recorded-destination resolution (C-2) --------------------------------------------------------------------
    /// <summary>The destination a document's LAST "Filed" event records. A business filing writes <c>domain</c> +
    /// <c>siteId</c> + <c>library</c> into the event Details (the medical lane writes none of them), so absence — or
    /// any doubt — resolves to null, which every by-id sink member treats as the medical default. That default is
    /// correct for the entire pre-business corpus by construction: none of it carries a domain marker. BOTH recorded
    /// axes are PREFERRED over current configuration, so neither renaming the business library nor cutting over
    /// SP_BUSINESS_SITE_ID later can silently re-address history (review finding: recording the library alone left
    /// the site axis to live config, and a dedicated business site is the anticipated next step). Residue, stated
    /// honestly: business Filed events minted before the siteId key existed fall back to the CONFIGURED site on that
    /// axis — if a site cutover ever precedes their re-filing, resolve those documents by hand first. This is the
    /// read-side complement of FileAsync's target-aware write: a driveItemId is only meaningful within a drive, so
    /// whoever acts on a recorded locator must resolve the drive it was recorded in — a delete through the wrong
    /// drive 404s, the 404 folds to success, and the ledger records a removal that never happened.</summary>
    internal static async Task<Adelaide.SharePoint.FilingTarget?> RecordedFiledTargetAsync(IAuditLog audit, Adelaide.SharePoint.SharePointOptions? spOpt, string docId, CancellationToken ct)
    {
        if (spOpt is null) return null; // no config in scope (test seams) → the medical default
        try
        {
            var events = await audit.GetEventsAsync(docId, ct);
            for (var i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].Type != "Filed") continue;
                using var d = System.Text.Json.JsonDocument.Parse(events[i].Details);
                if (d.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && d.RootElement.TryGetProperty("domain", out var dom) && dom.ValueKind == System.Text.Json.JsonValueKind.String
                    && string.Equals(dom.GetString(), DocumentDomain.Business, StringComparison.OrdinalIgnoreCase))
                {
                    var t = spOpt.BusinessTarget();
                    var lib = d.RootElement.TryGetProperty("library", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.String
                              && !string.IsNullOrWhiteSpace(l.GetString()) ? l.GetString()! : t.Library;
                    var site = d.RootElement.TryGetProperty("siteId", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
                               && !string.IsNullOrWhiteSpace(s.GetString()) ? s.GetString()! : t.SiteId;
                    return t with { SiteId = site, Library = lib };
                }
                return null; // the last Filed is medical (no domain marker)
            }
            return null; // never filed → medical default semantics
        }
        catch { return null; } // event-details parse doubt (our own writer, effectively unreachable) → medical default
    }

    /// <summary>Rebuild a <see cref="Adelaide.SharePoint.FilingTarget"/> from the destination an intent sidecar
    /// recorded, or null (→ medical default) when the intent predates the C-2 fix or the document was medical.
    /// Only SiteId/Library matter to the by-id members; ContentType/RequiresPatient are filing-time concerns.</summary>
    internal static Adelaide.SharePoint.FilingTarget? IntentTarget(string? siteId, string? library) =>
        !string.IsNullOrWhiteSpace(siteId) && !string.IsNullOrWhiteSpace(library)
            ? new Adelaide.SharePoint.FilingTarget(siteId, library, ContentType: null, RequiresPatient: false)
            : null;

    // ---- Supersede outbox (the DELETE half of the supersede slice) — mirrors the filing sidecar family exactly --------
    /// <summary>The delete INTENT recorded before superseding: the FULL terminal payload so recovery never re-derives.
    /// SupersededBy is the NEW doc's id; DriveItemIdToDelete is the OLD doc's durable locator; Actor is the true reviewer
    /// (preserved through recovery, like the filing sidecar's reviewer). TargetSiteId/TargetLibrary are the drive the
    /// locator was RECORDED in (C-2), resolved from the OLD doc's Filed event at intent-write time — null means the
    /// medical default: right for every medical document; a pre-fix sidecar naming a business document keeps the old
    /// wrong-drive behaviour until it drains.</summary>
    internal sealed record SupersedeIntent(string OldDocId, string DriveItemIdToDelete, string SupersededBy, string Actor, string Reason, string? TargetSiteId = null, string? TargetLibrary = null);

    internal static string SupersedeSidecarPath(string newPdfPath) => Path.ChangeExtension(newPdfPath, ".supersede.json");

    /// <summary>Record the supersede delete intent BEFORE the Graph delete, so a crash anywhere in delete→terminal-append
    /// is recovered idempotently by reconcile. Written next to the NEW doc's captured PDF; the .supersede.json suffix
    /// survives the new-doc input purge (which removes only .pdf/.filing.json/.review.json).</summary>
    internal static async Task WriteSupersedeSidecarAsync(string newPdfPath, SupersedeIntent intent, CancellationToken ct) =>
        await File.WriteAllTextAsync(SupersedeSidecarPath(newPdfPath),
            Json(new { oldDocId = intent.OldDocId, driveItemIdToDelete = intent.DriveItemIdToDelete, supersededBy = intent.SupersededBy, actor = intent.Actor, reason = intent.Reason, targetSiteId = intent.TargetSiteId, targetLibrary = intent.TargetLibrary }), ct);

    /// <summary>Read a supersede intent back for recovery. Null if absent/corrupt → reconcile leaves the file untouched
    /// (a bad intent must NEVER trigger a delete). The target keys are optional so every pre-C-2 sidecar still on disk
    /// reads back with a null destination — the medical default: correct for every MEDICAL document; a pre-fix intent
    /// that names a BUSINESS document keeps the old wrong-drive behaviour until it drains (intents are short-lived by
    /// design, and the state/locator tripwires catch the moved-document shapes).</summary>
    internal static SupersedeIntent? ReadSupersedeSidecar(string newPdfPath)
    {
        var path = SupersedeSidecarPath(newPdfPath);
        if (!File.Exists(path)) return null;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var r = d.RootElement;
            return new SupersedeIntent(
                r.GetProperty("oldDocId").GetString()!,
                r.GetProperty("driveItemIdToDelete").GetString()!,
                r.GetProperty("supersededBy").GetString()!,
                r.TryGetProperty("actor", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString()! : "reconcile",
                r.TryGetProperty("reason", out var rs) && rs.ValueKind == System.Text.Json.JsonValueKind.String ? rs.GetString()! : "vom Nutzer ersetzt",
                r.TryGetProperty("targetSiteId", out var tsi) && tsi.ValueKind == System.Text.Json.JsonValueKind.String ? tsi.GetString() : null,
                r.TryGetProperty("targetLibrary", out var tli) && tli.ValueKind == System.Text.Json.JsonValueKind.String ? tli.GetString() : null);
        }
        catch { return null; }
    }

    internal static void DeleteSupersedeSidecar(string newPdfPath)
    {
        try { File.Delete(SupersedeSidecarPath(newPdfPath)); } catch { /* best effort */ }
    }

    /// <summary>Execute (or re-drive) a recorded supersede: verify the intent is still ACTIONABLE (the tripwires — a
    /// stale intent must refuse, not "succeed"), delete the OLD item by its durable driveItemId in its RECORDED drive
    /// (404 = already gone = success, folded in GraphDeleteAsync), THEN append the OLD doc's terminal "Superseded"
    /// event — STATE-GUARDED on a FRESH re-read (skip if the doc is already Superseded) so a reconcile re-drive after
    /// a crash between the delete and the append can NEVER double-append. Removes the intent sidecar on completion.
    /// Fully idempotent: safe to call any number of times for one intent. The Superseded event's Details answer
    /// "where did the old doc go?" from the chain alone. Returns TRUE only on a genuine completion — a tripwire
    /// refusal returns false and keeps the sidecar, so the reconcile pass never counts a refusal as recovered work
    /// (same contract as <see cref="DriveReclassifyAsync"/>).</summary>
    internal static async Task<bool> DriveSupersedeAsync(IFiler sink, IAuditLog audit, string newPdfPath, SupersedeIntent intent,
        Microsoft.Extensions.Logging.ILogger log, CancellationToken ct)
    {
        // Read the OLD doc BEFORE the delete: both tripwires need the ledger's view of the predecessor while nothing
        // has been destroyed yet. (The at-most-once state guard below does its OWN re-read after the delete — the
        // freshest possible decision, as before this guard existed.)
        var old = await audit.GetDocumentAsync(intent.OldDocId, ct);
        // Locator tripwire (B-1), the mirror image of DriveReclassifyAsync's: reclassify refuses while the ledger
        // STILL names the intent's locator (its write-ahead event is missing); a supersede intent is stale when the
        // ledger NO LONGER names it — the predecessor was reclassified and re-filed underneath the intent, so the
        // recorded delete would land on a dead id (404 → folds to success) and the terminal append would mark a
        // document whose REAL item is live. Refuse, keep the sidecar, and log for a human — same posture, same
        // words, as the reclassify guard. The error repeats every sweep until someone resolves it; that is the intent.
        if (old is { State: "Filed", DriveItemId.Length: > 0 }
            && !string.Equals(old.DriveItemId, intent.DriveItemIdToDelete, StringComparison.OrdinalIgnoreCase))
        {
            log.LogError("Supersede intent for {DocId} names a locator the ledger no longer has it Filed at — the document re-filed since the intent was recorded. Refusing to delete and keeping the sidecar; this needs a human.", intent.OldDocId);
            return false;
        }
        // State tripwire: the intent's premise — validated by SupersedeGuard at submit — is a predecessor that is
        // FILED. "Superseded" is the one other actionable state (an idempotent re-drive completing a crash between
        // the append and the sidecar delete). Any OTHER state means the document LEFT Filed after the intent was
        // recorded (a reprocess is mid-pipeline, a discard landed, a split fanned out) and BOTH halves are stale:
        // the delete targets an item the ledger no longer vouches for, and the terminal append would land over a
        // live or otherwise-disposed document — the resurrection this family of guards exists to prevent. The
        // predecessor claim (review submit + re-drive) cannot cover this ordering: nothing holds the claim in the
        // gap between the reprocess releasing it and this re-drive firing.
        if (old is not null && old.State is not ("Filed" or "Superseded"))
        {
            log.LogError("Supersede intent for {DocId} found the document in a state that is no longer Filed — it moved since the intent was recorded. Refusing to delete and keeping the sidecar; this needs a human.", intent.OldDocId);
            return false;
        }
        await sink.TryDeleteByIdAsync(intent.DriveItemIdToDelete, IntentTarget(intent.TargetSiteId, intent.TargetLibrary), ct); // id-only; 404 → success (idempotent re-drive); drive = the intent's recorded destination
        // At-most-once guard on a FRESH read (not the pre-delete snapshot): two supersedes of one predecessor are
        // unsynchronized until the predecessor claim lands, and a stale snapshot here would widen their
        // double-append window by a full Graph round-trip.
        var cur = await audit.GetDocumentAsync(intent.OldDocId, ct);
        if (cur is not null && cur.State != "Superseded") // state-guard: the terminal event is appended AT MOST once
            await audit.AppendAsync(new AuditEntry(intent.OldDocId, "Superseded", intent.Actor, Details: Json(new
            {
                supersededBy = intent.SupersededBy,
                driveItemIdDeleted = intent.DriveItemIdToDelete,
                actor = intent.Actor,
                reason = intent.Reason,
            })), ct);
        // The old doc's grace-deferred inbox inputs are NOT deleted here — deliberately. A supersede is a human
        // discovering a mistake and correcting it: exactly the event class the C5 grace window insures, so a
        // supersede-done-in-error must keep its arrival copy for the same window an ordinary misfile gets (the
        // mis-split history is the precedent: the parent PDF was the ONLY source to re-derive a mis-split). The
        // aged sweep's Superseded branch releases these inputs after the window, verified via the SUCCESSOR's
        // driveItemId (this item is intentionally gone). With no grace window configured there is nothing to do:
        // the inputs were already purged at the old doc's own filing.
        DeleteSupersedeSidecar(newPdfPath);
        // Graph driveItem delete goes to the site Recycle Bin — verified against the production tenant (every supersede
        // deletion checked matched a bin entry by timestamp). The window is at most 93 days across both stages:
        // Microsoft's default, owner-attested as never changed there, NOT measured — the Graph
        // recycleBin settings resource does not answer for this site. It is a ceiling, not a promise (either stage can
        // be emptied early), so neither this log nor any user text offers a specific recovery window.
        log.LogInformation("Superseded {OldDocId} by {NewDocId}; old item removed from SharePoint.", intent.OldDocId, intent.SupersededBy);
        return true;
    }

    // ---- Reclassify outbox (the DELETE half of /api/reprocess) — the fourth member of the supersede-outbox family -----
    /// <summary>The delete intent recorded during a reclassify: the OLD item's durable locator, so the removal can be
    /// re-driven after a crash or a transient Graph failure. Reclassify was the ONE SharePoint delete outside this
    /// family — the gap that let a failed delete leave the previous item live while the document re-filed, producing a
    /// duplicate. TargetSiteId/TargetLibrary are the drive the locator was RECORDED in (C-2), resolved from the doc's
    /// Filed event at the endpoint — null on medical documents and on every pre-C-2 sidecar (→ the medical default).</summary>
    internal sealed record ReclassifyIntent(string DocId, string DriveItemIdToDelete, string Actor, string? TargetSiteId = null, string? TargetLibrary = null);

    internal static string ReclassifySidecarPath(string pdfPath) => Path.ChangeExtension(pdfPath, ".reclassify.json");

    /// <summary>Record the reclassify delete intent. ORDERING IS LOAD-BEARING: this is written AFTER the write-ahead
    /// "Received" event, never before. A sidecar that existed while the ledger still said Filed would let reconcile
    /// delete the item out from under a terminal record — precisely the silent loss this whole slice removes.</summary>
    internal static async Task WriteReclassifySidecarAsync(string pdfPath, ReclassifyIntent intent, CancellationToken ct) =>
        await File.WriteAllTextAsync(ReclassifySidecarPath(pdfPath),
            Json(new { docId = intent.DocId, driveItemIdToDelete = intent.DriveItemIdToDelete, actor = intent.Actor, targetSiteId = intent.TargetSiteId, targetLibrary = intent.TargetLibrary }), ct);

    /// <summary>Read a reclassify intent back for recovery. Null if absent/corrupt → reconcile leaves the file untouched
    /// (a bad intent must NEVER trigger a delete — same rule as the supersede sidecar).</summary>
    internal static ReclassifyIntent? ReadReclassifySidecar(string pdfPath)
    {
        var path = ReclassifySidecarPath(pdfPath);
        if (!File.Exists(path)) return null;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var r = d.RootElement;
            return new ReclassifyIntent(
                r.GetProperty("docId").GetString()!,
                r.GetProperty("driveItemIdToDelete").GetString()!,
                r.TryGetProperty("actor", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString()! : "reconcile",
                r.TryGetProperty("targetSiteId", out var tsi) && tsi.ValueKind == System.Text.Json.JsonValueKind.String ? tsi.GetString() : null,
                r.TryGetProperty("targetLibrary", out var tli) && tli.ValueKind == System.Text.Json.JsonValueKind.String ? tli.GetString() : null);
        }
        catch { return null; }
    }

    internal static void DeleteReclassifySidecar(string pdfPath)
    {
        try { File.Delete(ReclassifySidecarPath(pdfPath)); } catch { /* best effort */ }
    }

    /// <summary>Execute (or re-drive) a recorded reclassify delete: remove the OLD item by its durable driveItemId
    /// (404 = already gone = success, folded in GraphDeleteAsync), then drop the intent. Fully idempotent — safe to call
    /// any number of times for one intent.
    ///
    /// It appends an ANNOTATION (<see cref="AuditEventTypes.SharePointItemRemoved"/>), NOT an ordinary event. The
    /// distinction is the point: an ordinary append would set the document's State to its own type, and a late
    /// "Received" landing after the document had progressed to Transcribed/Filed would reset it and re-run the
    /// pipeline. An annotation chains identically — Seq, PrevHash, tamper-evidence, EventCount — while leaving State
    /// alone, which is what makes it safe to append at any point in the lifecycle. Contrast
    /// <see cref="DriveSupersedeAsync"/>, whose recovery must guarantee a TERMINAL event and so appends an ordinary one.
    ///
    /// WHAT IS AND IS NOT IDEMPOTENT: the Graph delete is (404 folds to success, so repeating it is free). The append
    /// is NOT, and cannot be guarded the way supersede guards its terminal event — `old.State != "Superseded"` works
    /// there precisely because that append MOVES State, and an annotation by construction does not. So the family's
    /// one protection against re-drive double-append is unavailable here and is replaced by an at-most-once check
    /// keyed on the INTENT. Without it, a sidecar that cannot be deleted (read-only attribute, ACL, or a foreign
    /// FileShare.Read handle — readable but undeletable) mints one immutable row per reconcile tick, ~720 a day, into
    /// an append-only ledger with no prune path.
    ///
    /// SAFETY GUARD: refuse to delete while the ledger still says the document is Filed AT THIS VERY LOCATOR. That
    /// combination means the write-ahead event is missing, so deleting would recreate the exact silent loss this family
    /// exists to prevent. The endpoint's ordering makes it unreachable; it is the tripwire behind that ordering.
    ///
    /// Returns true only on a GENUINE completion, so a refusal is not counted as recovered work.</summary>
    internal static async Task<bool> DriveReclassifyAsync(IFiler sink, IAuditStore audit, string pdfPath, ReclassifyIntent intent,
        Microsoft.Extensions.Logging.ILogger log, CancellationToken ct)
    {
        var doc = await audit.GetDocumentAsync(intent.DocId, ct);
        if (doc is not null && doc.State == "Filed"
            && string.Equals(doc.DriveItemId, intent.DriveItemIdToDelete, StringComparison.OrdinalIgnoreCase))
        {
            log.LogError("Reclassify intent for {DocId} names the locator the ledger still has it Filed at — refusing to delete and keeping the sidecar. The write-ahead event is missing; this needs a human.", intent.DocId);
            return false;
        }

        await sink.TryDeleteByIdAsync(intent.DriveItemIdToDelete, IntentTarget(intent.TargetSiteId, intent.TargetLibrary), ct); // id-only; 404 → success (idempotent re-drive); drive = the intent's recorded destination

        // No document to annotate — an orphan intent (its projection row never existed, or was erased). Complete the
        // removal and drop the intent, but do NOT append: the store's insert branch would mint a projection row whose
        // State is the annotation's own type, i.e. a document in a state that is not part of the lifecycle vocabulary.
        if (doc is null)
        {
            log.LogWarning("Reclassify intent for {DocId} has no projection row; item removed and intent dropped without a chain record.", intent.DocId);
            DeleteReclassifySidecar(pdfPath);
            return true;
        }

        // AT-MOST-ONCE, keyed on the intent (see above). Only this path reaches here — the endpoint deletes inline and
        // states its own outcome — so an annotation records that the sweep observed the item gone.
        var alreadyRecorded = (await audit.GetEventsAsync(intent.DocId, ct)).Any(e =>
            e.Type == AuditEventTypes.SharePointItemRemoved
            && e.Details.Contains(intent.DriveItemIdToDelete, StringComparison.Ordinal));
        if (!alreadyRecorded)
            await audit.AppendAsync(new AuditEntry(intent.DocId, AuditEventTypes.SharePointItemRemoved, intent.Actor,
                Details: Json(new { driveItemIdDeleted = intent.DriveItemIdToDelete, channel = "Reclassify" })), ct);

        DeleteReclassifySidecar(pdfPath);
        log.LogInformation("Reclassify delete completed for {DocId}; previous item removed from SharePoint.", intent.DocId);
        return true;
    }

    // ---- Rotate outbox (Phase-1 in-place transform) — the SIMPLEST member of the supersede-outbox family --------------
    /// <summary>A recorded page-rotation intent: the ABSOLUTE /Rotate target per 1-based page, the pre-rotation content
    /// hash (so the byte change is auditable and the pre-transform bytes identifiable), and the true reviewer. Rotation
    /// is in-place (no new doc-id) and idempotent (absolute targets), so this is lighter than the supersede intent.</summary>
    internal sealed record RotateIntent(string DocId, IReadOnlyDictionary<int, int> Angles, string PreHash, string Actor);

    internal static string RotateSidecarPath(string pdfPath) => Path.ChangeExtension(pdfPath, ".rotate.json");

    /// <summary>Record the rotation intent BEFORE mutating the PDF bytes, so a crash in apply→Rotated-append is recovered
    /// idempotently by reconcile. The .rotate.json suffix survives the filed-input purge (removes only
    /// .pdf/.filing.json/.review.json).</summary>
    internal static async Task WriteRotateSidecarAsync(string pdfPath, RotateIntent intent, CancellationToken ct) =>
        await File.WriteAllTextAsync(RotateSidecarPath(pdfPath),
            Json(new { docId = intent.DocId, angles = intent.Angles, preHash = intent.PreHash, actor = intent.Actor }), ct);

    /// <summary>Read a rotation intent back for recovery. Null if absent/corrupt → reconcile leaves it untouched (a bad
    /// intent must NEVER trigger a byte mutation).</summary>
    internal static RotateIntent? ReadRotateSidecar(string pdfPath)
    {
        var path = RotateSidecarPath(pdfPath);
        if (!File.Exists(path)) return null;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var r = d.RootElement;
            var angles = new Dictionary<int, int>();
            foreach (var p in r.GetProperty("angles").EnumerateObject())
                if (int.TryParse(p.Name, out var pg) && p.Value.ValueKind == System.Text.Json.JsonValueKind.Number) angles[pg] = p.Value.GetInt32();
            if (angles.Count == 0) return null;
            return new RotateIntent(
                r.GetProperty("docId").GetString()!,
                angles,
                r.TryGetProperty("preHash", out var ph) && ph.ValueKind == System.Text.Json.JsonValueKind.String ? ph.GetString()! : "",
                r.TryGetProperty("actor", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString()! : "reconcile");
        }
        catch { return null; }
    }

    internal static void DeleteRotateSidecar(string pdfPath)
    {
        try { File.Delete(RotateSidecarPath(pdfPath)); } catch { /* best effort */ }
    }

    // ---- Split outbox (the FAN-OUT half of /api/split) — the fifth member of the supersede-outbox family -------------
    /// <summary>One unit of a recorded split: the child id, its 1-based inclusive page range in the parent, and the
    /// display name the reviewer chose (or the auto "(Teil i/n)" label) — everything needed to re-derive the child
    /// from the parent PDF without the original request.</summary>
    internal sealed record SplitUnit(string ChildId, int First, int Last, string OriginalName);

    /// <summary>The fan-out intent recorded BEFORE the split's Segmented commit. Deliberately NOT the commit itself —
    /// the Segmented event is (C-3) — so the recovery pass acts only when that event exists: with it, missing children
    /// are re-derived from the recorded ranges and re-enqueued; without it, the commit aborted and the intent is
    /// dropped. OriginalName can carry reviewer-facing document naming, which is fine on the deletable inbox (the
    /// filing sidecar already carries the transcript).</summary>
    internal sealed record SplitIntent(string ParentDocId, string SeparatorVersion, string Actor, IReadOnlyList<SplitUnit> Units);

    internal static string SplitSidecarPath(string pdfPath) => Path.ChangeExtension(pdfPath, ".split.json");

    internal static async Task WriteSplitSidecarAsync(string pdfPath, SplitIntent intent, CancellationToken ct) =>
        await File.WriteAllTextAsync(SplitSidecarPath(pdfPath),
            Json(new
            {
                parentDocId = intent.ParentDocId,
                separatorVersion = intent.SeparatorVersion,
                actor = intent.Actor,
                units = intent.Units.Select(u => new { childId = u.ChildId, first = u.First, last = u.Last, originalName = u.OriginalName }).ToArray(),
            }), ct);

    /// <summary>Read a split intent back for recovery. Null if absent/corrupt → reconcile leaves the file untouched
    /// (same family rule: a bad intent must never drive a derivation).</summary>
    internal static SplitIntent? ReadSplitSidecar(string pdfPath)
    {
        var path = SplitSidecarPath(pdfPath);
        if (!File.Exists(path)) return null;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var r = d.RootElement;
            var units = new List<SplitUnit>();
            foreach (var u in r.GetProperty("units").EnumerateArray())
                units.Add(new SplitUnit(
                    u.GetProperty("childId").GetString()!,
                    u.GetProperty("first").GetInt32(),
                    u.GetProperty("last").GetInt32(),
                    u.TryGetProperty("originalName", out var on) && on.ValueKind == System.Text.Json.JsonValueKind.String ? on.GetString()! : u.GetProperty("childId").GetString()!));
            if (units.Count == 0) return null; // a unitless split intent is corrupt by construction
            return new SplitIntent(
                r.GetProperty("parentDocId").GetString()!,
                r.TryGetProperty("separatorVersion", out var sv) && sv.ValueKind == System.Text.Json.JsonValueKind.String ? sv.GetString()! : "split-manual-v1",
                r.TryGetProperty("actor", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString()! : "reconcile",
                units);
        }
        catch { return null; }
    }

    internal static void DeleteSplitSidecar(string pdfPath)
    {
        try { File.Delete(SplitSidecarPath(pdfPath)); } catch { /* best effort */ }
    }

    /// <summary>Complete (or discard) a recorded split fan-out. The Segmented event is the COMMIT: when the parent's
    /// chain has it, every recorded unit is verified — a missing child PDF is re-derived from the parent bytes by its
    /// recorded range (with the parent's saved orientation snapshotted on, as the endpoint does), a missing projection
    /// row gets its Received append, and a child THIS pass minted is enqueued (a child in Received from an earlier
    /// partial fan-out is left to the startup sweep, exactly as before — enqueuing it here could double-process one
    /// the in-memory queue still holds). Transcript slices are deliberately NOT re-derived: the review sidecar may
    /// already be gone, and a child without a carried transcript transcribes fresh — the endpoint's own safe fallback.
    /// When the chain has NO Segmented event, the commit aborted (the endpoint surfaced an error and nothing durable
    /// exists) and the intent is dropped. Returns true only when a committed fan-out was verified/completed.</summary>
    private async Task<bool> DriveSplitRecoveryAsync(string inboxDir, string parentPdfPath, SplitIntent intent, CancellationToken ct)
    {
        var committed = (await audit.GetEventsAsync(intent.ParentDocId, ct)).Any(e => e.Type == "Segmented");
        if (!committed)
        {
            DeleteSplitSidecar(parentPdfPath);
            log.LogInformation("Split intent for {DocId} has no committed Segmented event — aborted commit, intent dropped.", intent.ParentDocId);
            return false;
        }
        if (!File.Exists(parentPdfPath))
        {
            // Committed but underivable: the parent PDF is the ONLY source for the children's bytes. Keep the intent
            // and say so — same loud-until-human posture as the other tripwires in this family.
            log.LogError("Split intent for {DocId} is committed but the parent PDF is gone — missing children cannot be derived. Keeping the sidecar; this needs a human.", intent.ParentDocId);
            return false;
        }
        var parent = await audit.GetDocumentAsync(intent.ParentDocId, ct);
        var parentBytes = await File.ReadAllBytesAsync(parentPdfPath, ct);
        var fileSha = parent?.Sha256 ?? intent.ParentDocId;
        var parentSavedRotation = ReadSavedRotationAngle(parentPdfPath);
        foreach (var u in intent.Units)
        {
            var childPath = Path.GetFullPath(Path.Combine(inboxDir, $"{u.ChildId}.pdf"));
            if (!File.Exists(childPath))
            {
                var childBytes = Adelaide.Ai.PdfPageOps.ExtractRange(parentBytes, u.First, u.Last);
                await File.WriteAllBytesAsync(childPath, childBytes, CancellationToken.None);
                if (parentSavedRotation != 0)
                    await WriteSavedRotationAsync(childPath, parentSavedRotation, "human", CancellationToken.None);
            }
            var minted = await store.GetDocumentAsync(u.ChildId, ct) is null;
            if (minted)
                await audit.AppendAsync(new AuditEntry(u.ChildId, "Received", intent.Actor, Details: Json(new
                {
                    channel = "Split",
                    sha256 = Sha256Hex(await File.ReadAllBytesAsync(childPath, CancellationToken.None)),
                    splitFrom = intent.ParentDocId,
                    pageRange = new PageRange(u.First, u.Last).ToString(),
                    derivationKey = UnitKeys.DerivationKey(fileSha, new PageRange(u.First, u.Last), intent.SeparatorVersion),
                }), Projection: new ProjectionOverrides(OriginalName: u.OriginalName)), CancellationToken.None);
            if (minted)
                await queue.Writer.WriteAsync(new UploadJob(u.ChildId, childPath, u.OriginalName, Origin: JobOrigin.ReconcileRedrive), CancellationToken.None);
        }
        DeleteSplitSidecar(parentPdfPath);
        log.LogInformation("Split recovery completed the fan-out for {DocId}: {UnitCount} recorded unit(s) verified or re-derived.", intent.ParentDocId, intent.Units.Count);
        return true;
    }

    // ---- Saved rotation (persisted document orientation) — review WORKING STATE, NOT the crash-safe bake intent --------
    // The reviewer-confirmed orientation of an in-review document, persisted the instant they rotate (PUT
    // /api/documents/{id}/rotation) so it survives leaving and re-opening the queue — an objective document FACT (like the
    // detected type or matched patient), not a per-session view setting. Distinct from .rotate.json (the at-commit bake
    // INTENT): this .rotation.json is the DISPLAYED angle that /api/page defaults to and that the submit reads to bake.
    // Shape { pages: { "*": deg }, source }: one doc-wide absolute angle today under "*", but per-page-ready. source
    // "human" = a reviewer set it (a human orientation must win over any future "auto" guess). Lives beside the inbox PDF
    // and is purged with it at filing (see PurgeFiledInputsAsync). Auto-saving is safe ONLY because orientation is
    // objectively correct-or-not — this pattern must NEVER be generalised to type/patient (those are judgements).
    internal static string SavedRotationSidecarPath(string pdfPath) => Path.ChangeExtension(pdfPath, ".rotation.json");

    /// <summary>Persist the document-wide orientation (absolute 0/90/180/270). Written on every reviewer rotate click and
    /// snapshotted onto split children. Kept even at 0 so a human-confirmed "upright" outranks a future auto guess.
    /// ATOMIC (temp + move) so a concurrent /api/page read never sees a half-written file.</summary>
    internal static async Task WriteSavedRotationAsync(string pdfPath, int angle, string source, CancellationToken ct)
    {
        var deg = ((angle % 360) + 360) % 360;
        var path = SavedRotationSidecarPath(pdfPath);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, Json(new { pages = new Dictionary<string, int> { ["*"] = deg }, source }), ct);
        File.Move(tmp, path, overwrite: true); // atomic swap — a reader sees the whole old or whole new file, never a torn one
    }

    /// <summary>Read the saved document-wide angle back (the "*" entry). 0 when absent/corrupt/blank — the safe default
    /// (unrotated). NEVER throws: a bad sidecar must not break page rendering.</summary>
    internal static int ReadSavedRotationAngle(string pdfPath)
    {
        var path = SavedRotationSidecarPath(pdfPath);
        if (!File.Exists(path)) return 0;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (d.RootElement.TryGetProperty("pages", out var pages) && pages.ValueKind == System.Text.Json.JsonValueKind.Object
                && pages.TryGetProperty("*", out var star) && star.ValueKind == System.Text.Json.JsonValueKind.Number)
                return ((star.GetInt32() % 360) + 360) % 360;
            return 0;
        }
        catch { return 0; }
    }

    internal static void DeleteSavedRotation(string pdfPath)
    {
        try { File.Delete(SavedRotationSidecarPath(pdfPath)); } catch { /* best effort */ }
    }

    // ---- Carried transcript (split/segment reuse) ----
    // A split child inherits ITS slice of the PARENT's transcript instead of paying a fresh vision transcription:
    // /api/split writes this sidecar (the child's own pages' RAW markdown), ProcessAsync consumes it at the transcribe
    // step and SKIPS the gpt-4o call — re-decoding barcodes LOCALLY on the child's pages so a child never inherits a
    // sibling page's KVNR. Absent ⇒ the child transcribes normally (the safe fallback: an older/mismatch parent, a lost
    // sidecar). Consumed-then-deleted at transcribe; survives a crash so a reconcile re-enqueue still reuses it.
    internal sealed record CarriedTranscript(IReadOnlyList<string> PageMarkdown, double Legibility, string ParentDocId, string SeparatorVersion);

    internal static string PreTranscriptSidecarPath(string pdfPath) => Path.ChangeExtension(pdfPath, ".pretranscript.json");

    internal static async Task WritePreTranscriptAsync(string pdfPath, CarriedTranscript carried, CancellationToken ct) =>
        await File.WriteAllTextAsync(PreTranscriptSidecarPath(pdfPath),
            Json(new { pageMarkdown = carried.PageMarkdown, legibility = carried.Legibility, parentDocId = carried.ParentDocId, separatorVersion = carried.SeparatorVersion }), ct);

    /// <summary>Read a carried transcript back. Null if absent/corrupt/empty → the child transcribes fresh (safe fallback).</summary>
    internal static CarriedTranscript? ReadPreTranscript(string pdfPath)
    {
        var path = PreTranscriptSidecarPath(pdfPath);
        if (!File.Exists(path)) return null;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var r = d.RootElement;
            if (!r.TryGetProperty("pageMarkdown", out var pm) || pm.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var pages = new List<string>();
            foreach (var el in pm.EnumerateArray())
                pages.Add(el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString()! : "");
            if (pages.Count == 0) return null;
            return new CarriedTranscript(
                pages,
                r.TryGetProperty("legibility", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.Number ? Math.Clamp(l.GetDouble(), 0, 1) : 0.5,
                r.TryGetProperty("parentDocId", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String ? p.GetString()! : "",
                r.TryGetProperty("separatorVersion", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString()! : "");
        }
        catch { return null; }
    }

    internal static void DeletePreTranscript(string pdfPath)
    {
        try { File.Delete(PreTranscriptSidecarPath(pdfPath)); } catch { /* best effort */ }
    }

    /// <summary>Local barcode pass on a child's OWN pages (render + free/local decode) — the same scan the transcriber
    /// runs, used when a carried transcript lets us skip the vision call but the child still needs its barcodes
    /// (KVNR identity) freshly decoded from its own bytes.</summary>
    internal static IReadOnlyList<DecodedBarcode> ScanBarcodesLocal(string pdfPath)
    {
        try
        {
            var pages = Adelaide.Ai.PdfRenderer.Render(pdfPath, 300, 15);
            try { return Adelaide.Ai.BarcodeScanner.Scan(pages); }
            finally { foreach (var p in pages) p.Dispose(); }
        }
        catch { return Array.Empty<DecodedBarcode>(); } // best-effort: a render/scan hiccup must NOT crash a transcript reuse — text-based KVNR still applies downstream
    }

    /// <summary>Execute (or re-drive) a recorded rotation: apply the ABSOLUTE /Rotate targets to the inbox PDF (idempotent
    /// — re-applying the same absolute angles is a no-op) and append a STATE-GUARDED "Rotated" event (skip if the doc is
    /// already "Rotated", so a reconcile re-drive after a crash between the write-back and the append can NEVER
    /// double-append). The write-back is ATOMIC (temp + move) and happens ONLY after a successful rotation, so a PDF
    /// PdfSharp cannot parse throws BEFORE any byte change — the caller then leaves the original untouched and surfaces an
    /// error. Removes the intent on completion; a failure AFTER the byte swap leaves the intent for the callers/sweep to
    /// re-drive, and the moot branch below drops an intent ONLY once its event is in the chain or the bytes never
    /// changed — an un-audited byte change is never silently forgotten. The event carries pre/post content hashes +
    /// per-page angles + <c>unit</c> (the activated UnitId), so the transform is auditable from the chain alone and the
    /// pre-rotation bytes stay reconstructible (zero the /Rotate).</summary>
    internal static async Task DriveRotateAsync(IAuditLog audit, string pdfPath, RotateIntent intent,
        Microsoft.Extensions.Logging.ILogger log, CancellationToken ct)
    {
        // At-most-once + moot guard: append only while the doc is still AWAITING rotation (a review state). Once it
        // has advanced to/past the Rotated append (Rotated/Reviewed/Filing), reached ANY terminal state
        // (DocumentStates.Settled — inherited, so Segmented/SeededHistory are covered; a hand-written list here
        // once omitted Segmented, and a lingering intent then appended Rotated to a terminal parent, flipping the
        // projection live and letting reconcile re-file already-filed content — A2), or is parked for a human
        // (DeadLetter — deliberately moot: applying a rotation would advance a parked document out of its triage
        // queue), the rotation must not re-mutate. (Rotated advances State transiently like Reviewed; a crash that
        // leaves State=Rotated self-heals via the reconcile re-enqueue.)
        var priorDoc = await audit.GetDocumentAsync(intent.DocId, ct);
        if (priorDoc is null || DocumentStates.IsSettled(priorDoc.State) || priorDoc.State is "Rotated" or "Reviewed" or "Filing" or "DeadLetter")
        {
            // Moot for the DOCUMENT — but the INTENT may still be the only record of an un-audited byte change:
            // the doc can advance past Rotated precisely BECAUSE the append failed (bytes swapped, no event), and
            // this branch used to destroy that last evidence ("the event is already recorded or the rotation is
            // moot" was not exhaustive — C-1). Drop the intent only when it is genuinely spent: its Rotated event
            // is in the chain (matched on the intent's own PreHash), or the on-disk bytes still hash to PreHash
            // (the swap never happened), or there is nothing left to audit (no doc / no PDF). Otherwise KEEP it
            // and say so — the sweep re-encounters it every tick until a human resolves it; that is the intent.
            if (priorDoc is not null && File.Exists(pdfPath))
            {
                var recorded = (await audit.GetEventsAsync(intent.DocId, ct))
                    .Any(e => e.Type == "Rotated" && e.Details.Contains(intent.PreHash, StringComparison.Ordinal));
                if (!recorded && !string.Equals(Sha256Hex(await File.ReadAllBytesAsync(pdfPath, ct)), intent.PreHash, StringComparison.OrdinalIgnoreCase))
                {
                    log.LogError("Rotate intent for {DocId}: the document has advanced ({State}) but its bytes were changed with NO Rotated event in the chain — an un-audited rotation. Keeping the sidecar; this needs a human.", intent.DocId, priorDoc.State);
                    return;
                }
            }
            DeleteRotateSidecar(pdfPath);
            return;
        }
        var bytes = await File.ReadAllBytesAsync(pdfPath, ct);
        var rotated = Adelaide.Ai.PdfRotation.WithRotations(bytes, intent.Angles); // absolute → idempotent; throws on malformed (fail-safe: nothing written yet)
        var tmp = pdfPath + ".rot.tmp";
        await File.WriteAllBytesAsync(tmp, rotated, ct);
        File.Move(tmp, pdfPath, overwrite: true); // atomic swap — never a torn PDF on crash-during-write
        await audit.AppendAsync(new AuditEntry(intent.DocId, "Rotated", intent.Actor, UnitId: intent.DocId, Details: Json(new
        {
            pages = intent.Angles,
            preHash = intent.PreHash,
            postHash = Sha256Hex(rotated),
            actor = intent.Actor,
        })), ct);
        DeleteRotateSidecar(pdfPath);
        log.LogInformation("Rotated {DocId}: {PageCount} page(s) set to an absolute /Rotate.", intent.DocId, intent.Angles.Count);
    }

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>After a CONFIRMED Filed, drop the inbox inputs (captured PDF + any review sidecar) so the inbox holds only
    /// in-flight/review docs — not a permanent second copy of the patient PDF (PHI minimisation + cost on the Azure Files
    /// mount). VERIFIED: deletes only once the item is confirmed present in SharePoint BY ITS driveItemId (Stage III); on
    /// ANY uncertainty it KEEPS the local copy (reclassify rehydrates it by id). Runs only at the terminal Filed state, so
    /// the crash-safe outbox is unaffected. Logs the opaque DocId, never the filename (KVNR).</summary>
    internal static Task PurgeFiledInputsAsync(IFiler sink, string docId, string pdfPath, string driveItemId, Adelaide.SharePoint.FilingTarget? target, Microsoft.Extensions.Logging.ILogger log, CancellationToken ct) =>
        PurgeFiledInputsAsync(sink, docId, pdfPath, driveItemId, target, log, ct, PurgeGate.Enabled, FiledPurgeGrace.Deferred);

    /// <summary>Gate-explicit overload (verification finding): the gates are read-once statics, so tests inject the
    /// decisions here; production always resolves them from the statics via the wrapper above.</summary>
    internal static async Task PurgeFiledInputsAsync(IFiler sink, string docId, string pdfPath, string driveItemId, Adelaide.SharePoint.FilingTarget? target, Microsoft.Extensions.Logging.ILogger log, CancellationToken ct,
        bool purgeEnabled, bool graceDeferred)
    {
        if (!purgeEnabled) // evidence-preservation kill-switch (fail-closed): keep the local pre-file copy
        {
            log.LogInformation("Filed-input purge DISABLED (ADELAIDE_PURGE_ENABLED not set) — preserving inbox inputs for {DocId}.", docId);
            return;
        }
        if (graceDeferred) // C5: a grace window is configured — the aged sweep owns deletion, not the filing path
        {
            log.LogDebug("Filed-input purge deferred for {DocId}: {Days}d grace window active; the reconcile tick's aged sweep purges after the window.", docId, FiledPurgeGrace.Days);
            return;
        }
        await PurgeFiledInputsCoreAsync(sink, docId, pdfPath, driveItemId, target, log, ct);
    }

    /// <summary>The gate-free verify-and-delete core, shared by the at-filing purge and the C5 aged sweep (each
    /// applies its own gates before calling). Semantics unchanged: verified-by-driveItemId or nothing is deleted.
    /// Returns TRUE only when the captured PDF was actually deleted after a positive SharePoint verify — every
    /// keep/skip/failure path returns false, so callers can count real releases (verification finding: the sweep's
    /// summary log must not claim releases the core refused).</summary>
    internal static async Task<bool> PurgeFiledInputsCoreAsync(IFiler sink, string docId, string pdfPath, string driveItemId, Adelaide.SharePoint.FilingTarget? target, Microsoft.Extensions.Logging.ILogger log, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(driveItemId)) // invariant: a just-filed doc always has an id; defensive — keep the copy
            {
                log.LogWarning("Filed-input purge skipped for {DocId}: no driveItemId to verify against; keeping local copy.", docId);
                return false;
            }
            if (!await sink.ExistsByIdAsync(driveItemId, target, ct)) // verify in the drive the locator was RECORDED in (C-2) — a business item probed medically reads as absent and its inputs never release
            {
                log.LogWarning("Filed-input purge skipped for {DocId}: item not confirmed in SharePoint; keeping local copy.", docId);
                return false;
            }
            // SIDECARS FIRST, THE PDF LAST (C-7). The aged sweep enumerates *.pdf only, so a sidecar orphaned by a
            // failure AFTER the PDF delete is visited by NOTHING, ever — and .review.json and .pretranscript.json
            // carry the transcript (PHI). PDF-last is self-healing instead of self-orphaning: a failure leaves the
            // PDF with stale sidecars, and the next sweep re-visits and finishes.
            try { File.Delete(Path.ChangeExtension(pdfPath, ".review.json")); } catch (Exception delEx) { log.LogWarning("Filed-input purge could not delete review sidecar for {DocId} ({Error}); it remains on disk.", docId, delEx.GetType().Name); }
            DeleteSavedRotation(pdfPath); // best-effort: the saved orientation is baked at filing; clear the review-only .rotation.json
            DeletePreTranscript(pdfPath); // best-effort: normally consumed at transcribe; clears any stray carried-transcript sidecar
            var deleted = false; // tracks the PDF delete specifically — the aged sweep counts real releases on it
            try { File.Delete(pdfPath); deleted = true; } catch (Exception delEx) { log.LogWarning("Filed-input purge could not delete captured PDF for {DocId} ({Error}); it remains on disk.", docId, delEx.GetType().Name); }
            if (deleted) log.LogInformation("Purged inbox inputs for {DocId} after verified filing.", docId);
            return deleted;
        }
        catch (Exception ex)
        {
            // Verify failed (e.g. a Graph hiccup) → keep the local copy; retaining it is harmless and reclassify can use it.
            log.LogWarning("Filed-input purge could not verify {DocId} in SharePoint; keeping local copy. {ExType} [{ExChain}] httpStatus={HttpStatus} graphReqId={GraphRequestId}",
                docId, ex.GetType().Name, Redact.Chain(ex), ex.Data["httpStatus"], ex.Data["graphRequestId"]);
            return false;
        }
    }

    /// <summary>The Segmented branch's release core (split-parent windowing): the review sidecar, saved
    /// orientation and any carried pre-transcript first, THE PDF LAST (C-7: the sweep enumerates *.pdf, so
    /// PDF-last is self-healing rather than self-orphaning), with NO SharePoint verify — a split parent was
    /// never filed and has no durable copy to verify against; the window alone is the release condition
    /// (decision D2, split-parent windowing). Returns TRUE only when the PDF was actually deleted, so the
    /// sweep's summary counts real releases.</summary>
    internal static bool ReleaseSplitParentInputs(string docId, string pdfPath, Microsoft.Extensions.Logging.ILogger log)
    {
        try { File.Delete(Path.ChangeExtension(pdfPath, ".review.json")); } catch (Exception delEx) { log.LogWarning("Split-parent release could not delete the review sidecar for {DocId} ({Error}); it remains on disk.", docId, delEx.GetType().Name); }
        DeleteSavedRotation(pdfPath);
        DeletePreTranscript(pdfPath);
        var deleted = false;
        try { File.Delete(pdfPath); deleted = true; } catch (Exception delEx) { log.LogWarning("Split-parent release could not delete the parent PDF for {DocId} ({Error}); it remains on disk.", docId, delEx.GetType().Name); }
        if (deleted) log.LogInformation("Released split-parent inputs for {DocId} after the repair window.", docId);
        return deleted;
    }
}

// Review submit payload (camelCase JSON from the Angular client). SupersedeTarget (optional) turns this filing into a
// SUPERSEDE: after the new doc is filed, the named existing doc is deleted from SharePoint and marked Superseded. It
// MUST equal this doc's recorded duplicateOf (validated server-side) — never a general delete lever.
// IsBusiness is the explicit domain discriminator (design §12 Phase 1): the reviewer marked this a non-patient
// "Geschäftsdokument", so it files to the business holding library with no patient — NEVER inferred from an empty
// PatientListItemId. Business submissions relax the patient guard and cannot supersede (validated in ReviewGuard).
// Zielbereich is the reviewer's functional-area label (HR/Finance/unklar) for a business submission — REQUIRED for a
// business filing (ReviewGuard), recorded on the immutable Reviewed event (the system of record; see ReviewDiff) and
// copied best-effort to the SharePoint Zielbereich column. Null/ignored on the medical path.
// Rotation is NO LONGER carried on this payload: orientation is a persisted document property (.rotation.json, saved per
// click via PUT /api/documents/{id}/rotation) that the submit READS and bakes — so the reviewer's angle survives leaving
// the queue, and the submit can never file an orientation the payload disagrees with. See the bake in POST /api/review.
// FieldProvenance (WP4, docs/CAPTURE.md) is the client's OBSERVED last-writer map per field — prefill | patient |
// typed — i.e. how each value got into the form, which the structural MATCH/CORRECTION/FILL inference cannot see.
// Optional (older clients omit it), validated server-side (ReviewCapture.SanitizeProvenance: known field names,
// known enum values, nothing else) before it touches the ledger or a store. Observational only — no decision reads it.
record ReviewSubmission(string DocumentId, string ContentType, int PatientListItemId, Dictionary<string, string?> Fields, bool AcknowledgedDuplicate = false, string? SupersedeTarget = null, bool IsBusiness = false, string? Zielbereich = null, Dictionary<string, string>? FieldProvenance = null);

// Manual split payload: the reviewer partitions an in-review document into contiguous 1-based page ranges [[first,last],…]
// that must cover all pages with no gaps/overlaps. Each range becomes a child unit that re-enters the pipeline.
record SplitSubmission(string DocumentId, IReadOnlyList<int[]>? Ranges, IReadOnlyList<string>? Names = null);

// Undiscard payload: the free-text WHY (required non-empty; audit value lives in the erasable projection, never
// in the append-only event payload — C6).
record UndiscardBody(string? Reason);

// Rotation-save payload: the reviewer's chosen absolute document orientation (0/90/180/270). Persisted to .rotation.json
// via PUT /api/documents/{id}/rotation, defaulted by /api/page, baked at submit. Absolute (not a delta) → the save is idempotent.
record RotationBody(int Angle);

// Supersede validation — the PURE core of the /api/review supersede guard, extracted so it is unit-testable without
// hosting the endpoint. Given the requested target id, the SOURCE doc's recorded duplicateOf id, and the resolved
// TARGET doc, it returns exactly one outcome; the endpoint maps each to a German HTTP response. Validating at submit
// time (not compare time) is deliberate: two pending duplicates of one original can race, and the target may have been
// discarded/superseded between opening the compare screen and submitting.
static class SupersedeGuard
{
    internal enum Result { Ok, NotSupersede, MismatchDuplicate, TargetNotFiled, TargetNoLocator }

    internal static Result Validate(string? supersedeTarget, string? duplicateOfDocId, DocumentAudit? target)
    {
        if (string.IsNullOrEmpty(supersedeTarget)) return Result.NotSupersede;
        // The target MUST be THIS doc's own recorded duplicateOf — never a general delete lever. A source with no
        // duplicateOf (a DuplicateSuspect — which has no review sidecar anyway — or a plain NeedsReview) yields a null
        // id here → mismatch → rejected. This is the endpoint-level half of "DuplicateSuspect never supersedes".
        if (duplicateOfDocId is null || !string.Equals(duplicateOfDocId, supersedeTarget, StringComparison.Ordinal))
            return Result.MismatchDuplicate;
        if (target is null || target.State != "Filed") return Result.TargetNotFiled; // stale: discarded / already superseded / never filed
        if (string.IsNullOrEmpty(target.DriveItemId)) return Result.TargetNoLocator; // legacy pre-backfill → id-only delete impossible
        return Result.Ok;
    }
}

// The conditional patient guard — the PURE core of POST /api/review's up-front validation, extracted (like
// SupersedeGuard) so the "medical requires a patient, business does not" rule is unit-testable without hosting the
// endpoint. A business submission (design §12 Phase 1) files to the holding library with NO patient, so the patient
// requirement is relaxed; it also cannot supersede (a business doc never replaces a medical filing). "Business" is the
// EXPLICIT IsBusiness discriminator — never inferred from an empty PatientListItemId.
static class ReviewGuard
{
    internal enum Result { Ok, ContentTypeRequired, PatientRequired, BusinessCannotSupersede, ZielbereichRequired, UnknownContentType, InvalidFieldName }

    // The functional-area labels a BUSINESS submission may carry. The SharePoint Zielbereich column keeps the FULL
    // vocabulary (incl. "Patient") for Phase-2 reuse, but the non-patient business ACTION offers/accepts only this
    // subset — "Patient" is nonsensical for a document the reviewer just declared non-patient.
    internal static readonly string[] BusinessZielbereiche = { "HR", "Finance", "unklar" };

    /// <summary>Field NAMES may enter the append-only Reviewed event (correctedFields/fieldSignals), so they must be
    /// identifier-shaped (C6 verification finding): ASCII letter start, letters/digits/underscore, ≤64 chars. Every
    /// catalog column and identity field passes; free prose, names with spaces/umlauts, and oversized strings do not —
    /// a crafted or buggy client cannot mint arbitrary text into the hash-chained ledger via a Fields key.</summary>
    internal static bool ValidFieldName(string name) =>
        name.Length is > 0 and <= 64 && char.IsAsciiLetter(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>The extractor's CLOSED field-name vocabulary — everything a legitimate medical submission can carry
    /// (second verification round: the per-submitted-type allowlist 400'd real reviewer flows, because the client
    /// submits its whole seeded values map): the UNION of all catalog columns across types (a type CORRECTION
    /// leaves the original type's extracted keys in the submission), the identity block (plus DocumentDate — the
    /// form's fifth fixed identity input, which Leistungsnachweis alone has no column for), and the server-added
    /// provenance fields (Domain/ContentType discriminators, OriginalContentType from a classify self-check
    /// overturn, Leistungen from the BFB barcode). Compile-time closed — the extractor maintains it, so the list
    /// costs no upkeep, and an alphanumeric pseudo-name still fails it where the charset floor alone would not.</summary>
    internal static readonly IReadOnlySet<string> LedgerSafeFieldNames = BuildLedgerSafeFieldNames();

    // ONE definition, in Adelaide.Core, so the extractor enforces the SAME set this endpoint does. It used to be
    // built here, which is why the extractor could not apply it and had to defer to a backstop that did not cover
    // the fallback type — see FieldVocabulary.
    private static HashSet<string> BuildLedgerSafeFieldNames() =>
        new(FieldVocabulary.Columns, StringComparer.OrdinalIgnoreCase);

    /// <summary>The BUSINESS lane's closed vocabulary. It was previously OPEN — the charset floor was the only gate,
    /// so an identifier-shaped client-chosen key reached the append-only Reviewed event's fieldProvenance[].field
    /// (verification finding; any authenticated reviewer can set isBusiness). It is the medical union PLUS the
    /// business filing columns, and it has to be: the reviewer only re-labels the DOMAIN, so the submission still
    /// carries the whole values map the ORIGINAL medical classification seeded — the same reason the medical union
    /// is a union across types rather than the submitted type's columns. Derived, not enumerated, from the same two
    /// sources the code already treats as authoritative (DocumentCatalog, SharePointSink.BusinessFieldNames).</summary>
    internal static readonly IReadOnlySet<string> BusinessLedgerSafeFieldNames = BuildBusinessLedgerSafeFieldNames();

    private static HashSet<string> BuildBusinessLedgerSafeFieldNames()
    {
        var v = new HashSet<string>(LedgerSafeFieldNames, StringComparer.OrdinalIgnoreCase);
        v.UnionWith(Adelaide.SharePoint.SharePointSink.BusinessFieldNames);
        return v;
    }

    /// <summary>The closed vocabulary for a lane — the ONE accessor <see cref="Prune"/> and <see cref="Validate"/>
    /// both read. They disagreed before (Prune applied a vocabulary only on the medical lane; Validate returned from
    /// the business branch before reaching its gate at all), which is exactly how the business lane ended up with no
    /// vocabulary. One accessor makes that divergence unrepresentable.</summary>
    internal static IReadOnlySet<string> VocabularyFor(bool isBusiness) =>
        isBusiness ? BusinessLedgerSafeFieldNames : LedgerSafeFieldNames;

    /// <summary>The ONE ledger-safety test for a field NAME: the identifier charset floor AND the lane's closed
    /// vocabulary. <see cref="Prune"/> and <see cref="Validate"/> apply it to CLIENT-supplied keys;
    /// <see cref="ReviewDiff.ModelFieldMap"/> and <see cref="ReviewDiff.CorrectedFields"/> apply it to the MODEL's
    /// own sidecar keys, which nothing checked before. Both sources had to converge on one predicate: the client
    /// gate was never the binding constraint, because a name the model invented reached the same immutable keys
    /// without ever passing through a submission.</summary>
    internal static bool LedgerSafeFieldName(string name, bool isBusiness) =>
        ValidFieldName(name) && VocabularyFor(isBusiness).Contains(name);

    /// <summary>Drop submission field keys that must never reach the recording paths — non-identifier keys and
    /// non-vocabulary keys, on BOTH lanes now — instead of rejecting the submission. Such keys are model or
    /// sidecar PROVENANCE (a pre-fix hallucinated column name, a stale key from an older sidecar), not reviewer
    /// input: the reviewer cannot even see them, so a 400 would brick an otherwise correct human review, while
    /// dropping them meets the same goal — nothing CLIENT-CHOSEN outside the closed vocabulary reaches the
    /// hash-chained ledger. The CLIENT-CHOSEN qualifier used to be load-bearing: dropping a key here makes the field
    /// look CLEARED to <see cref="ReviewDiff.CorrectedFields"/> (present in the model map, absent from the
    /// submission), so an off-vocabulary name the MODEL produced still landed in `Reviewed.correctedFields`. That
    /// residual is now closed on the model side too — <see cref="ReviewDiff.ModelFieldMap"/> and
    /// <see cref="ReviewDiff.CorrectedFields"/> apply the same <see cref="LedgerSafeFieldName"/> predicate — so the
    /// guarantee no longer depends on which side a name came from.
    /// <see cref="Validate"/>'s own gates stay behind this as defense-in-depth for callers that skip the prune.</summary>
    internal static ReviewSubmission Prune(ReviewSubmission body, out int dropped)
    {
        dropped = 0;
        bool Keep(string k) => LedgerSafeFieldName(k, body.IsBusiness);

        // BOTH client-controlled key maps, independently. FieldProvenance used to be filtered only as a SIDE EFFECT
        // of a Fields key being dropped — two early returns above it meant that the ordinary submission (well-formed
        // Fields, nothing to drop) carried its provenance keys through completely ungated. They were then stopped
        // one layer down, by SanitizeProvenance's known-names filter, which is a thinner guarantee than this
        // function's own docstring claimed. Gate them here, where the vocabulary lives.
        var keptFields = body.Fields is { Count: > 0 }
            ? body.Fields.Where(kv => Keep(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : body.Fields;
        var keptProv = body.FieldProvenance is { Count: > 0 }
            ? body.FieldProvenance.Where(kv => Keep(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : body.FieldProvenance;

        dropped = (body.Fields?.Count ?? 0) - (keptFields?.Count ?? 0)
                + (body.FieldProvenance?.Count ?? 0) - (keptProv?.Count ?? 0);
        if (dropped == 0) return body;
        return body with { Fields = keptFields!, FieldProvenance = keptProv };
    }

    internal static Result Validate(ReviewSubmission body)
    {
        if (string.IsNullOrWhiteSpace(body.ContentType)) return Result.ContentTypeRequired;
        // Fields KEYS are ledger inputs on BOTH lanes (values never are): they survive into the append-only Reviewed
        // event's correctedFields/fieldSignals/fieldProvenance[].field. So both gates run before the lane fork —
        // the charset floor AND the lane's closed vocabulary. The business lane used to reach neither vocabulary
        // gate (it returned from its branch below, and Prune's predicate exempted it), which left an
        // identifier-shaped client-chosen token as a write channel into an immutable table. The endpoint PRUNES
        // stray provenance keys before calling this; the gate is the defense-in-depth behind the prune, and it
        // closes what the charset floor cannot — an alphanumeric pseudo-name is a valid identifier, not a field.
        {
            bool Bad(string k) => !LedgerSafeFieldName(k, body.IsBusiness);
            // Both client-controlled key maps. FieldProvenance keys reach fieldProvenance[].field on the same event,
            // so leaving them out of this gate would have made the medical lane's own guarantee narrower than stated.
            if (body.Fields is not null && body.Fields.Keys.Any(Bad)) return Result.InvalidFieldName;
            if (body.FieldProvenance is not null && body.FieldProvenance.Keys.Any(Bad)) return Result.InvalidFieldName;
        }
        if (body.IsBusiness)
        {
            if (!string.IsNullOrEmpty(body.SupersedeTarget)) return Result.BusinessCannotSupersede;
            // Required + validated against the allowed set: the label IS the point of the business action (the Phase-2
            // training signal), so an unset/invalid Zielbereich is refused rather than filed unlabeled.
            return Array.Exists(BusinessZielbereiche, z => string.Equals(z, body.Zielbereich, StringComparison.Ordinal))
                ? Result.Ok : Result.ZielbereichRequired;
        }
        // Medical lane: the type must be a CATALOG type (C6 verification finding). Previously a non-catalog string
        // merely skipped the required-fields check and then flowed verbatim into the hash-chained Filing/Filed/
        // Reviewed Details — an un-erasable free-text channel. The business lane keeps its own vocabulary (the spec
        // takes the target's type, not this string), mirrored by the Zielbereich gate above.
        if (DocumentCatalog.Find(body.ContentType) is null) return Result.UnknownContentType;
        return body.PatientListItemId > 0 ? Result.Ok : Result.PatientRequired;
    }
}

// The model-vs-human correction record on the "Reviewed" audit event (PHI-free; forward-only — sealed history untouched).
// Patient fields: NAMES ONLY (correctedFields), never was/now VALUES (KVNR/surname/DOB/dates). contentType is the ONE
// EXCEPTION — a document category, not patient data — so `contentType` (the FINAL/corrected type, the rate-slice
// dimension) and, ONLY when the type was corrected, `contentTypeChange {from,to}` (the classification confusion matrix)
// are recorded with values. Readers tolerate BOTH shapes (recovery treats the record as an opaque object); nothing reads
// was/now back.
static class ReviewDiff
{
    public static object Compute(System.Text.Json.Nodes.JsonNode model, ReviewSubmission human)
    {
        var modelType = model["contentType"]?.GetValue<string>();
        // The domain classifier's proposal, if this document was extracted after the domain step shipped. Recording it
        // on BOTH lanes turns the reviewer's checkbox into a labelled confirm/correct signal: agreement is as
        // informative as disagreement, and without the agreements the denominator for domain accuracy is missing.
        var modelDomain = model["domain"]?.GetValue<string>();
        var humanDomain = human.IsBusiness ? DocumentDomain.Business : DocumentDomain.Medical;
        // null (a pre-domain-step document) is NOT agreement — left null so those are excluded, not counted correct.
        bool? domainConfirmed = modelDomain is null ? null : string.Equals(modelDomain, humanDomain, StringComparison.OrdinalIgnoreCase);
        // Business reassignment (design §11): the reviewer moved this unit OUT of the medical domain. Record the domain
        // flip as first-class labeled data — model said medical-type X → human said Business — the seed for the future
        // domain classifier. PHI-free: the medical contentType is a category, "Business" is a lane label. Deliberately
        // NOT the medical field-level diff (those columns don't apply once the domain changes).
        if (human.IsBusiness)
        {
            var businessRecord = new Dictionary<string, object?>
            {
                ["domain"] = "Business",
                ["domainChange"] = new { from = modelType, to = "Business" },
                // The domain decision as a MEASURABLE pair: what the model proposed and whether the human kept it.
                ["modelDomain"] = modelDomain,
                ["domainConfirmed"] = domainConfirmed,
                // The reviewer's functional-area routing decision (HR/Finance/unklar) — the human-verified label the
                // Phase-2 domain classifier is trained/measured against. PHI-free (a lane category). THIS is the label's
                // system of record: it rides the immutable Reviewed event AND the filing sidecar's review.diff, so a
                // reconciled/recovered business filing keeps it independent of the (best-effort) SharePoint column.
                ["zielbereich"] = human.Zielbereich,
            };
            AddProvenance(businessRecord, model, human);
            return businessRecord;
        }
        var record = new Dictionary<string, object?>
        {
            // Field NAMES only (camelCase, PHI-free); includes "contentType" when the type was corrected. Empty = a review
            // that changed nothing (recorded anyway → the rate DENOMINATOR is all reviews, not just corrected ones).
            // KEPT AS-IS for back-compat: existing readers and the PHI forward-strip depend on this shape. What it
            // could never say is WHY a field changed, which fieldSignals below adds alongside it.
            ["correctedFields"] = CorrectedFields(model, human),
            // Why each changed field changed: match | correction | fill. A patient selection populates the identity
            // fields from the patient record, which used to be indistinguishable from a human correcting the model —
            // and put KVNR/BirthDate/LastName/FirstName in the top four of the "sharpen these prompts" ranking purely
            // as an artefact. See ReviewFieldSignal.
            ["fieldSignals"] = FieldSignals(model, human),
            // The extraction-accuracy ORACLE: what the model read for each identity field vs what the patient record
            // says, for the patient this document was matched to. Free, deterministic ground truth from two values
            // already in hand. Field name + agreed + a STRUCTURAL descriptor of the difference; never a value.
            ["identityAgreement"] = IdentityAgreementSignals(model, human),
            // The FINAL/corrected type — the slice dimension, so a doc's stats attach to what it ACTUALLY IS, not the
            // model's mistaken guess. PHI-free (a category).
            ["contentType"] = human.ContentType,
            // Same domain pair on the medical lane: a document the model called Business and the human kept medical is
            // a domain error too, and was previously invisible because only business submissions recorded anything.
            ["domain"] = DocumentDomain.Medical,
            ["modelDomain"] = modelDomain,
            ["domainConfirmed"] = domainConfirmed,
        };
        // Classification confusion matrix — recorded ONLY when the type was corrected. contentType is NON-PHI, so this is
        // the one place a before→after value pair is captured.
        if (!string.Equals(modelType, human.ContentType, StringComparison.OrdinalIgnoreCase))
            record["contentTypeChange"] = new { from = modelType, to = human.ContentType };
        AddProvenance(record, model, human);
        return record;
    }

    /// <summary>WP4 (docs/CAPTURE.md): the OBSERVED last-writer per field — field NAMES + an enum, never a value, so
    /// it passes the same WORM boundary as correctedFields (ReviewDiffPhiStripTests pattern). Server-sanitized
    /// (known names, known writers) before it can enter the hash chain; absent for an old client, so every event
    /// they produce serialises exactly as before. Recorded ALONGSIDE the structural fieldSignals inference —
    /// deliberately not replacing it — so later analysis can compare observation against reconstruction.</summary>
    private static void AddProvenance(Dictionary<string, object?> record, System.Text.Json.Nodes.JsonNode model, ReviewSubmission human)
    {
        if (Adelaide.Web.ReviewCapture.SanitizeProvenance(model, human) is { Count: > 0 } prov)
            record["fieldProvenance"] = prov.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (object)new { field = Camel(kv.Key), provenance = kv.Value }).ToArray();
    }

    /// <summary>Tier A: the PHI-FREE, NORMALIZED set of field NAMES a human changed vs the model's original extraction —
    /// names ONLY, never before/after values. Normalization (dates via day-first parse, trim, case, null≡empty) means a
    /// non-edit is not miscounted: the same date re-typed in ISO vs dotted German form is NOT a correction. "contentType"
    /// and "patient" are recorded by NAME when reassigned. Empty result = a review that changed nothing (a real data
    /// point for the rate denominator: reviews-with-no-correction).</summary>
    /// <summary>Per-changed-field classification: match | correction | fill. Uses the SAME change detection as
    /// <see cref="CorrectedFields"/> so the two can never disagree about WHICH fields changed — only about what the
    /// change means.</summary>
    internal static object[] FieldSignals(System.Text.Json.Nodes.JsonNode model, ReviewSubmission human)
    {
        var modelFields = ModelFieldMap(model, human.IsBusiness);
        var patientLinked = human.PatientListItemId > 0;
        var changed = CorrectedFields(model, human);
        var signals = new List<object>();
        foreach (var camel in changed)
        {
            if (camel is "contentType" or "patient") continue;   // not extraction fields; tracked on their own keys
            // CorrectedFields camelCases the column name; recover the original column so the lookup matches.
            var column = modelFields.Keys.FirstOrDefault(k => string.Equals(Camel(k), camel, StringComparison.OrdinalIgnoreCase))
                         ?? (human.Fields ?? new Dictionary<string, string?>()).Keys.FirstOrDefault(k => string.Equals(Camel(k), camel, StringComparison.OrdinalIgnoreCase))
                         ?? camel;
            var proposed = modelFields.TryGetValue(column, out var mv) && !string.IsNullOrWhiteSpace(mv);
            signals.Add(new { field = camel, signal = ReviewFieldSignals.Classify(column, proposed, patientLinked).Tag() });
        }
        return [.. signals];
    }

    /// <summary>Model-extracted identity values vs the matched patient record's values. Computable only when the
    /// submission kept the model's matched candidate — that is the one case where the record's values are already on
    /// the sidecar. When the reviewer picked a DIFFERENT patient we do not hold that patient's values here, and this
    /// returns nothing rather than guessing: a wrong oracle is worse than a missing one.</summary>
    internal static object[] IdentityAgreementSignals(System.Text.Json.Nodes.JsonNode model, ReviewSubmission human)
    {
        var cand = model["match"]?["candidate"];
        if (cand is null) return [];
        var candId = cand["listItemId"]?.GetValue<int>();
        if (candId is null || candId != human.PatientListItemId) return [];   // reviewer chose someone else

        var modelFields = ModelFieldMap(model, human.IsBusiness);
        var recordValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["KVNR"] = cand["kvnr"]?.GetValue<string>(),
            ["LastName"] = cand["lastName"]?.GetValue<string>(),
            ["FirstName"] = cand["firstName"]?.GetValue<string>(),
            ["BirthDate"] = cand["birthDate"]?.GetValue<string>(),
        };

        var outp = new List<object>();
        foreach (var (column, recordValue) in recordValues)
        {
            modelFields.TryGetValue(column, out var modelValue);
            if (IdentityAgreement.Compare(column, modelValue, recordValue) is { } r)
                outp.Add(new { field = r.Field, agreed = r.Agreed, shape = r.Shape });
        }
        return [.. outp];
    }

    /// <summary>The model sidecar's field map, FILTERED to ledger-safe names — for the paths that write the
    /// append-only Reviewed event (<c>correctedFields</c>, <c>fieldSignals[].field</c>, and the canonical key behind
    /// <c>fieldProvenance[].field</c>). Field NAMES are treated as inherently non-identifying by every downstream
    /// reader, and a model-authored one carries no such guarantee.
    ///
    /// SCOPE, precisely: this is the last gate before the LEDGER, not the first gate overall. The real enforcement
    /// point is the extractor (<see cref="FieldVocabulary"/>), because the intake capture write persists the raw
    /// extracted names into <c>corpus_fields</c> before any review. This filter remains as defense-in-depth for
    /// sidecars extracted BEFORE that gate existed, which are still being reviewed.
    ///
    /// Dropping (rather than placeholder-substituting) also removes a measurement artefact: a model key pruned from
    /// the submission looked CLEARED to <see cref="CorrectedFields"/> — present in the model map, absent from the
    /// human map — and was counted as a human correction the reviewer never made.</summary>
    internal static Dictionary<string, string?> ModelFieldMap(System.Text.Json.Nodes.JsonNode model, bool isBusiness)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, v) in ModelFieldMapRaw(model))
            if (ReviewGuard.LedgerSafeFieldName(n, isBusiness)) map[n] = v;
        return map;
    }

    /// <summary>The sidecar's field map EXACTLY as extracted — no vocabulary filter. For the CAPTURE stores only,
    /// which are not the append-only ledger and which already hold the raw extracted name: <c>corpus_fields</c> rows
    /// are written at intake keyed by that name, and the review outcome path stamps them BY NAME. Filtering here
    /// would buy no confidentiality (the row already exists) while stranding every such row permanently NULL, i.e.
    /// in the 'pending' bucket that exists to mean "awaiting adjudication".</summary>
    internal static Dictionary<string, string?> ModelFieldMapRaw(System.Text.Json.Nodes.JsonNode model)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (model["fields"] is System.Text.Json.Nodes.JsonArray arr)
            foreach (var f in arr)
                if (f?["name"]?.GetValue<string>() is { Length: > 0 } n)
                    map[n] = f["value"]?.GetValue<string>();
        return map;
    }

    /// <summary>How many of the model sidecar's field names were refused by the ledger vocabulary. Counted, never
    /// named: a hallucinated "column" can be arbitrary text, so it is logged the same way <see cref="ReviewGuard.Prune"/>'s
    /// client-side drops are — the count is the observable, the string never leaves the sidecar.</summary>
    /// <summary>Re-apply the ledger field-name vocabulary to a diff READ BACK FROM A FILING SIDECAR, before reconcile
    /// replays it as a fresh hash-chained "Reviewed" event. Returns how many names were dropped (count only — a
    /// hallucinated column can be arbitrary text).
    ///
    /// WHY THIS EXISTS: the extractor gate (FieldVocabulary) constrains names going FORWARD, but a sidecar written
    /// BEFORE that gate shipped still sits on disk carrying whatever the model authored. Reconcile's recovery append
    /// is a NEW write to the immutable chain, not a replay of an old row — so without this, a pre-gate sidecar could
    /// still put a model-authored name into the ledger after the fix. Same predicate as every other gate, so the
    /// guarantee does not depend on which door the name came through.
    ///
    /// "contentType" and "patient" are kept explicitly: they are ReviewDiff's OWN tokens for a type correction and a
    /// patient reassignment, not catalog columns, so the vocabulary does not contain them and a naive filter would
    /// silently discard two legitimate correction signals. Lane is read from the diff's own `domain`, defaulting to
    /// MEDICAL — the stricter (subset) vocabulary — when a pre-domain-step diff does not carry one.</summary>
    internal static int SanitizeStoredDiff(System.Text.Json.Nodes.JsonObject diff)
    {
        var isBusiness = string.Equals(diff["domain"]?.GetValue<string>(), DocumentDomain.Business, StringComparison.OrdinalIgnoreCase);
        bool Safe(string? n) => n is not null
            && (n is "contentType" or "patient" || ReviewGuard.LedgerSafeFieldName(n, isBusiness));

        var dropped = 0;
        if (diff["correctedFields"] is System.Text.Json.Nodes.JsonArray cf)
        {
            var kept = new System.Text.Json.Nodes.JsonArray();
            foreach (var n in cf)
            {
                var s = n?.GetValue<string>();
                if (Safe(s)) kept.Add(System.Text.Json.Nodes.JsonValue.Create(s));
                else dropped++;
            }
            diff["correctedFields"] = kept;
        }
        // Every other shape that carries a field NAME. identityAgreement's names are the fixed identity block and so
        // always pass — filtered anyway, so a future shape cannot quietly become the one unchecked door.
        foreach (var key in new[] { "fieldSignals", "fieldProvenance", "identityAgreement" })
        {
            if (diff[key] is not System.Text.Json.Nodes.JsonArray arr) continue;
            var kept = new System.Text.Json.Nodes.JsonArray();
            foreach (var e in arr)
            {
                if (Safe(e?["field"]?.GetValue<string>())) kept.Add(e!.DeepClone());
                else dropped++;
            }
            diff[key] = kept;
        }
        return dropped;
    }

    internal static int OffVocabularyModelFields(System.Text.Json.Nodes.JsonNode model, bool isBusiness)
    {
        var n = 0;
        if (model["fields"] is System.Text.Json.Nodes.JsonArray arr)
            foreach (var f in arr)
                if (f?["name"]?.GetValue<string>() is { Length: > 0 } name && !ReviewGuard.LedgerSafeFieldName(name, isBusiness))
                    n++;
        return n;
    }

    internal static string[] CorrectedFields(System.Text.Json.Nodes.JsonNode model, ReviewSubmission human)
    {
        var corrected = new List<string>();
        var modelType = model["contentType"]?.GetValue<string>();
        if (!string.Equals(NormText(modelType), NormText(human.ContentType), StringComparison.OrdinalIgnoreCase))
            corrected.Add("contentType");
        int? modelPatient = model["match"]?["candidate"]?["listItemId"]?.GetValue<int>();
        if (modelPatient != human.PatientListItemId) corrected.Add("patient");

        var modelFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (model["fields"] is System.Text.Json.Nodes.JsonArray arr)
            foreach (var f in arr)
            {
                var name = f?["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(name)
                    // Model-authored names are ledger inputs here (they become correctedFields entries), and for the
                    // bare fallback type the extractor has no schema to have filtered them by. See ModelFieldMap.
                    && ReviewGuard.LedgerSafeFieldName(name, human.IsBusiness)
                    && !string.Equals(name, "ContentType", StringComparison.OrdinalIgnoreCase)          // covered by "contentType" above
                    && !string.Equals(name, "OriginalContentType", StringComparison.OrdinalIgnoreCase))
                    modelFields[name] = f?["value"]?.GetValue<string>();
            }

        var dateCols = DateColumns(human.ContentType); // Date-kind fields normalize via day-first parse before comparing
        var humanFields = human.Fields ?? new Dictionary<string, string?>();
        // Human keys are NOT re-filtered here, deliberately. They are already gated at the endpoint by
        // ReviewGuard.Prune/Validate, and the one caller that bypasses those — RecordRecoveredReviewAsync, replaying
        // a crashed filing's sidecar — supplies keys the server itself wrote from an already-pruned submission.
        // Filtering them again would silently diverge from ReviewCapture.ComputeFieldRecords, which builds its
        // record set from the same two maps: a sidecar naming a column since REMOVED from the catalog would drop out
        // of `changed` here while still getting a FieldRecord there, and be recorded as a confirmed MATCH with a null
        // proposal for a field the model and the reviewer actually disagreed on.
        var keys = new HashSet<string>(modelFields.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in humanFields.Keys) keys.Add(k);
        foreach (var k in keys)
        {
            var was = modelFields.TryGetValue(k, out var mv) ? mv : null;
            var now = humanFields.TryGetValue(k, out var hv) ? hv : null;
            var changed = dateCols.Contains(k)
                ? !DateEq(was, now)
                : !string.Equals(NormText(was), NormText(now), StringComparison.OrdinalIgnoreCase);
            if (changed) corrected.Add(Camel(k)); // camelCase the column name → consistent with "contentType"/"patient"
        }
        return corrected.ToArray();
    }

    private static string NormText(string? s) => (s ?? "").Trim();
    private static string Camel(string s) => string.IsNullOrEmpty(s) ? s
        : !s.Any(char.IsLower) ? s.ToLowerInvariant()          // all-caps acronym: KVNR → kvnr
        : char.ToLowerInvariant(s[0]) + s[1..];                // DocumentDate → documentDate

    // Mirrors AzureOpenAiExtractor.NormDate's day-first formats so DocumentDate/BirthDate/ValidFrom/… compare under the
    // SAME reading the pipeline stored them with (never a lenient/month-first parse). Neither side a date → text equality.
    private static readonly string[] DateFormats = { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "dd/MM/yyyy", "d/M/yyyy" };
    private static string? NormDate(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateTime.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateOnly.FromDateTime(dt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    private static bool DateEq(string? a, string? b)
    {
        var na = NormDate(a); var nb = NormDate(b);
        return na is not null || nb is not null
            ? string.Equals(na, nb, StringComparison.Ordinal)                              // ≥1 parses as a date → compare as dates
            : string.Equals(NormText(a), NormText(b), StringComparison.OrdinalIgnoreCase); // neither parses → text
    }

    // Date-kind columns for a content type: the type-specific Date fields from the catalog + the identity dates.
    private static HashSet<string> DateColumns(string? contentType)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DocumentDate", "BirthDate" };
        if (DocumentCatalog.Find(contentType ?? "") is { } def)
            foreach (var f in def.Fields)
                if (f.Kind == FieldKind.Date) set.Add(f.Column);
        return set;
    }
}

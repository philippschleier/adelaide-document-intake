using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Adelaide.Web;

// App Service "Easy Auth" integration, hand-rolled (no Microsoft.Identity.Web dependency — the same minimal idiom as the
// drain's JwtBearer wiring and KeyVaultBootstrap). Easy Auth terminates the Entra OIDC flow at the PLATFORM edge and
// forwards the already-validated identity in the X-MS-CLIENT-PRINCIPAL header (base64 JSON of the claims). This handler
// turns that header into HttpContext.User — including the Entra app-role claims — so the standard authorization policies
// (the Reviewer/Admin fallback) and fail-closed actor-stamping work unchanged.
//
// By default there is NO in-app token validation: the platform already validated the token, and the header is injected by
// the platform reverse proxy — it is not reachable from the public internet (App Service strips a client-sent copy). The
// security boundary is the platform (Easy Auth "require authentication") + Entra ("assignment required" on the app), which
// block non-members before a request ever reaches this process; this handler only READS the resulting identity.
//
// FINDING 3 (defence in depth): that model trusts a decoded header. When ADELAIDE_EASYAUTH_VALIDATE=true (plus the Easy
// Auth tenant + client id), the handler instead CRYPTOGRAPHICALLY validates a forwarded Entra token (signature via the
// tenant JWKS, issuer, audience, lifetime) and builds the identity from the VALIDATED token — so a forged
// X-MS-CLIENT-PRINCIPAL header is worthless even if a request ever reaches the app without the platform in front. It is
// OFF by default (no behaviour change) and REQUIRES the platform to forward the token (Easy Auth token store enabled →
// X-MS-TOKEN-AAD-ID-TOKEN). See the remediation summary for the platform change.
public static class EasyAuthDefaults
{
    public const string Scheme = "EasyAuth";
    public const string ReviewerRole = "Reviewer";
    public const string AdminRole = "Admin";
    public const string PrincipalHeader = "X-MS-CLIENT-PRINCIPAL";

    // The stable, non-PHI actor token stamped into the immutable audit: the Entra object id (oid) — NEVER the email or
    // UPN, which can change and would break attribution across a rename. Returns null when there is no authenticated
    // principal so callers FAIL CLOSED (refuse the mutation rather than record an anonymous/forged "who").
    public static string? Actor(ClaimsPrincipal? user)
    {
        var oid = user?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                  ?? user?.FindFirst("oid")?.Value;
        return string.IsNullOrEmpty(oid) ? null : "user:" + oid;
    }
}

public sealed class EasyAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IHostEnvironment env)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // FINDING 3: hardened path — trust ONLY a cryptographically-validated forwarded token. A missing token behaves as
        // unauthenticated (dev-actor in Development, else NoResult → challenge); a present-but-invalid token is a hard Fail.
        if (EasyAuthValidation.Enabled)
        {
            var token = Request.Headers[EasyAuthValidation.TokenHeader].ToString();
            if (string.IsNullOrEmpty(token)) return DevActorResult() ?? AuthenticateResult.NoResult();
            var principal = await EasyAuthValidation.ValidateAsync(token, Context.RequestAborted);
            if (principal is null)
            {
                Logger.LogWarning("Easy Auth token validation failed; rejecting the request.");
                return AuthenticateResult.Fail("Easy Auth token validation failed");
            }
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }

        // Default path (unchanged): read the platform-injected, already-validated principal header.
        var header = Request.Headers[EasyAuthDefaults.PrincipalHeader].ToString();
        if (string.IsNullOrEmpty(header))
            return DevActorResult() ?? AuthenticateResult.NoResult(); // anonymous → the fallback policy challenges

        List<Claim> claims;
        string nameTyp = ClaimTypes.Name, roleTyp = ClaimTypes.Role;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(header)));
            var root = doc.RootElement;
            if (root.TryGetProperty("name_typ", out var nt) && nt.ValueKind == JsonValueKind.String) nameTyp = nt.GetString()!;
            if (root.TryGetProperty("role_typ", out var rt) && rt.ValueKind == JsonValueKind.String) roleTyp = rt.GetString()!;
            claims = [];
            if (root.TryGetProperty("claims", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                    if (c.TryGetProperty("typ", out var typ) && c.TryGetProperty("val", out var val)
                        && typ.ValueKind == JsonValueKind.String && val.ValueKind == JsonValueKind.String)
                        claims.Add(new Claim(typ.GetString()!, val.GetString()!));
        }
        catch (Exception ex)
        {
            // A malformed header is a misconfiguration, not a user — log the TYPE only (no header contents) and fail.
            Logger.LogWarning("EasyAuth principal header could not be parsed: {Error}", ex.GetType().Name);
            return AuthenticateResult.Fail("invalid principal header");
        }

        // Normalise Entra app roles to ClaimTypes.Role so IsInRole / the role policies resolve regardless of the source
        // claim type: app roles arrive as "roles", while the header's role_typ may be the SOAP role URI. Robust the same
        // way the Drain policy is (accept either), so a token-version change can't silently drop everyone's access.
        foreach (var r in claims.Where(c => c.Type is "roles" or "role" || c.Type == roleTyp).Select(c => c.Value).Distinct().ToList())
            if (!claims.Any(c => c.Type == ClaimTypes.Role && c.Value == r))
                claims.Add(new Claim(ClaimTypes.Role, r));

        return Success(claims, nameTyp);
    }

    // Development-only fallback identity (both roles) so the review UI works on localhost when ADELAIDE_DEV_ACTOR is set.
    // NEVER mints an identity on a deployed host: the IsDevelopment gate makes that a CODE invariant, not just a
    // deployment convention — a stray ADELAIDE_DEV_ACTOR on a Production/Staging host can never mint an identity.
    private AuthenticateResult? DevActorResult()
    {
        var devActor = Environment.GetEnvironmentVariable("ADELAIDE_DEV_ACTOR");
        if (!env.IsDevelopment() || string.IsNullOrEmpty(devActor)) return null;
        return Success(new[]
        {
            new Claim("oid", devActor),
            new Claim(ClaimTypes.Name, devActor),
            new Claim(ClaimTypes.Role, EasyAuthDefaults.ReviewerRole),
            new Claim(ClaimTypes.Role, EasyAuthDefaults.AdminRole),
        }, ClaimTypes.Name);
    }

    private static AuthenticateResult Success(IEnumerable<Claim> claims, string nameType)
    {
        var identity = new ClaimsIdentity(claims, EasyAuthDefaults.Scheme, nameType, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), EasyAuthDefaults.Scheme));
    }
}

// FINDING 3: cryptographic validation of the forwarded Easy Auth token. Configured-or-off, and never breaks the default
// deployment (Enabled is false unless explicitly turned on with a tenant + client id). The OpenID metadata / JWKS is
// fetched once and cached by ConfigurationManager (auto-refreshed on rollover).
internal static class EasyAuthValidation
{
    internal static readonly bool Enabled;
    internal static readonly string TokenHeader;
    private static readonly string? Tenant;
    private static readonly string? ClientId;
    private static ConfigurationManager<OpenIdConnectConfiguration>? _manager;

    static EasyAuthValidation()
    {
        Tenant = Environment.GetEnvironmentVariable("ADELAIDE_EASYAUTH_TENANT_ID");
        ClientId = Environment.GetEnvironmentVariable("ADELAIDE_EASYAUTH_CLIENT_ID");
        TokenHeader = Environment.GetEnvironmentVariable("ADELAIDE_EASYAUTH_TOKEN_HEADER") is { Length: > 0 } th ? th : "X-MS-TOKEN-AAD-ID-TOKEN";
        Enabled = ResolveEnabled(Environment.GetEnvironmentVariable("ADELAIDE_EASYAUTH_VALIDATE"), Tenant, ClientId);
    }

    /// <summary>Pure resolution of the validation switch: ON only when explicitly requested AND a tenant + client id are
    /// present (otherwise there is nothing to validate against). Extracted so the gating is unit-testable.</summary>
    internal static bool ResolveEnabled(string? flag, string? tenant, string? clientId) =>
        string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(tenant) && !string.IsNullOrWhiteSpace(clientId);

    private static ConfigurationManager<OpenIdConnectConfiguration> Manager => _manager ??= new ConfigurationManager<OpenIdConnectConfiguration>(
        $"https://login.microsoftonline.com/{Tenant}/v2.0/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever());

    /// <summary>Validate a forwarded Entra token and return the resulting principal, or null on ANY failure (the caller
    /// treats null as a hard reject). Signature (tenant JWKS), issuer, audience and lifetime are all validated.</summary>
    internal static async Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken ct)
    {
        try
        {
            var config = await Manager.GetConfigurationAsync(ct);
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = new[] { $"https://login.microsoftonline.com/{Tenant}/v2.0", $"https://sts.windows.net/{Tenant}/" },
                ValidateAudience = true,
                ValidAudiences = new[] { ClientId!, $"api://{ClientId}" },
                ValidateLifetime = true,
                IssuerSigningKeys = config.SigningKeys,
                RoleClaimType = "roles",
                NameClaimType = "name",
            };
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
            if (!result.IsValid || result.ClaimsIdentity is null) return null;

            // Build the identity from the VALIDATED claims and normalise app roles ("roles") to ClaimTypes.Role so the
            // Reviewer/Admin policies resolve exactly as they do on the header path.
            var identity = new ClaimsIdentity(result.ClaimsIdentity.Claims, EasyAuthDefaults.Scheme, "name", ClaimTypes.Role);
            foreach (var r in identity.FindAll("roles").Select(c => c.Value).Distinct().ToList())
                if (!identity.HasClaim(ClaimTypes.Role, r))
                    identity.AddClaim(new Claim(ClaimTypes.Role, r));
            return new ClaimsPrincipal(identity);
        }
        catch
        {
            return null; // network/JWKS/parse/validation failure → reject (fail closed)
        }
    }
}

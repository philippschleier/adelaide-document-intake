using Adelaide.Core;

namespace Adelaide.SharePoint;

/// <summary>
/// Connection config for the SharePoint/Graph adapters. Every identifier is REQUIRED (env/.env/Key Vault) — there are
/// deliberately no built-in tenant/site defaults, so a misconfigured host fails at startup instead of silently talking
/// to someone else's tenant. Demo mode uses <see cref="DemoPlaceholder"/> and never contacts Microsoft Graph.
/// <para>The Business* members hold the Phase-1 business-document routing destination (design §7): a single holding
/// library for reviewer-filed, non-patient documents. Optional/defaulted so the medical path is unaffected — until a
/// dedicated business site id is configured, business docs file to a separate LIBRARY on the existing site.</para>
/// </summary>
public sealed record SharePointOptions(
    string TenantId, string ClientId, string ClientSecret, string SiteId,
    string BusinessLibrary = "Business – Eingang", string BusinessSiteId = "", string BusinessContentType = "Document")
{
    /// <summary>The Graph site id business documents file into: the dedicated business site when SP_BUSINESS_SITE_ID is
    /// set, else the same site as the medical corpus (a separate LIBRARY there). A distinct site is the compliance-preferred
    /// layout (different permissions/retention than the PHI corpus); this default keeps Phase 1 shippable before it exists.</summary>
    public string ResolvedBusinessSiteId => string.IsNullOrWhiteSpace(BusinessSiteId) ? SiteId : BusinessSiteId;

    /// <summary>The Phase-1 business holding destination as a <see cref="FilingTarget"/> — the configured site/library,
    /// the business content type, and RequiresPatient=false. Phase 2's IRoutingRules.Resolve(unit) will produce a target
    /// per document unit from the routing-config table (design §7); this is the single hard-coded holding row until then.</summary>
    public FilingTarget BusinessTarget() => new(ResolvedBusinessSiteId, BusinessLibrary, BusinessContentType, RequiresPatient: false);

    public static SharePointOptions Load()
    {
        var env = DotEnv.Read();
        string? Val(string key) => Environment.GetEnvironmentVariable(key) ?? (env.TryGetValue(key, out var v) ? v : null);

        string Req(string key) => Val(key) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"Missing {key} (set it in the environment or .env — see .env.example).");
        var tenant = Req("ENTRA_TENANT_ID");
        var client = Req("ENTRA_CLIENT_ID");
        var site   = Req("SP_SITE_ID");          // Graph composite site id: "<host>.sharepoint.com,<siteGuid>,<webGuid>"
        var secret = Req("ENTRA_CLIENT_SECRET");

        // Business holding destination (Phase 1: one library; design §7). Site defaults to the medical site (a separate
        // library there) until a dedicated business site id is configured; content type is applied best-effort at filing.
        var businessLibrary = Val("SP_BUSINESS_LIBRARY");
        if (string.IsNullOrWhiteSpace(businessLibrary)) businessLibrary = "Business – Eingang";
        var businessSite = Val("SP_BUSINESS_SITE_ID") ?? "";
        var businessContentType = Val("SP_BUSINESS_CONTENT_TYPE");
        if (string.IsNullOrWhiteSpace(businessContentType)) businessContentType = "Document";

        return new SharePointOptions(tenant, client, secret, site, businessLibrary, businessSite, businessContentType);
    }

    /// <summary>Inert options for demo mode: syntactically valid, obviously fake, and never used to reach Graph (demo
    /// mode rebinds every SharePoint seam to local adapters and blocks outbound HTTP).</summary>
    public static SharePointOptions DemoPlaceholder() =>
        new("demo-tenant", "demo-client", "", "demo.sharepoint.invalid,demo-site,demo-web");
}

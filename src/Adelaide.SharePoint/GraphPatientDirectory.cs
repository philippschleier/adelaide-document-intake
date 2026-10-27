using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Adelaide.Core;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Adelaide.SharePoint;

/// <summary>
/// Live <see cref="IPatientDirectory"/> backed by the SharePoint "Patients" list via Microsoft Graph (app-only).
/// KVNR-first (InsuranceNumber, indexed); else name+DOB (PatientLastName indexed + DateOfBirth compared client-side).
/// HttpClient is supplied by IHttpClientFactory (with a resilience handler); the token credential is a DI singleton.
/// JSON mapping + OData escaping live in <see cref="GraphMapping"/> (unit-tested).
/// </summary>
public sealed class GraphPatientDirectory : IPatientDirectory, IPatientSearch
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private static readonly string[] GraphScope = { "https://graph.microsoft.com/.default" };
    private const string FieldSelect = "InsuranceNumber,PatientLastName,PatientFirstName,DateOfBirth,PatientStatus,KlientID,PatientID";

    private readonly HttpClient _http;
    private readonly SharePointOptions _opt;
    private readonly TokenCredential _cred;
    private readonly ILogger<GraphPatientDirectory> _log;
    private string? _patientsListId;

    public GraphPatientDirectory(HttpClient http, SharePointOptions opt, TokenCredential cred, ILogger<GraphPatientDirectory> log)
    {
        _http = http;
        _opt = opt;
        _cred = cred;
        _log = log;
    }

    // KVNR-first, then name+DOB — the strategy lives in PatientMatching.MatchAsync (shared with the demo directory);
    // this adapter supplies only the two indexed list queries.
    public Task<PatientMatch> MatchAsync(ExtractedData data, CancellationToken ct = default) =>
        PatientMatching.MatchAsync(data,
            async (kvnr, c) => await QueryAsync($"fields/InsuranceNumber eq '{GraphMapping.Escape(kvnr)}'", c),
            async (last, c) => await QueryAsync($"fields/PatientLastName eq '{GraphMapping.Escape(last)}'", c), // indexed
            ct);

    /// <summary>Free-text lookup for the review patient picker: a full KVNR matches exactly, otherwise a surname
    /// prefix (PatientLastName is indexed). Returns up to <paramref name="max"/> candidates for the reviewer to pick.</summary>
    public async Task<IReadOnlyList<PatientRef>> SearchAsync(string query, int max = 25, CancellationToken ct = default)
    {
        query = (query ?? "").Trim();
        if (query.Length < 2) return Array.Empty<PatientRef>();
        var filter = LooksLikeKvnr(query)
            ? $"fields/InsuranceNumber eq '{GraphMapping.Escape(query.ToUpperInvariant())}'"
            : $"startswith(fields/PatientLastName,'{GraphMapping.Escape(query)}')";
        var hits = await QueryAsync(filter, ct);
        return hits.Take(max).ToList();
    }

    private static bool LooksLikeKvnr(string s) => s.Length == 10 && char.IsLetter(s[0]) && s.Skip(1).All(char.IsDigit);

    /// <summary>Debug: the raw InsuranceNumber + DateOfBirth strings as SharePoint returns them (to diagnose date/zone shifts).</summary>
    public async Task<(string Kvnr, string Dob)?> DebugRawAsync(string lastName, CancellationToken ct = default)
    {
        var listId = await PatientsListIdAsync(ct);
        var filter = Uri.EscapeDataString($"fields/PatientLastName eq '{GraphMapping.Escape(lastName)}'");
        var root = await GetAsync($"{GraphBase}/sites/{_opt.SiteId}/lists/{listId}/items?$expand=fields($select={FieldSelect})&$filter={filter}&$top=5", ct);
        foreach (var item in root.GetProperty("value").EnumerateArray())
        {
            var f = item.GetProperty("fields");
            var kvnr = f.TryGetProperty("InsuranceNumber", out var k) ? k.ToString() : "(missing)";
            var dob = f.TryGetProperty("DateOfBirth", out var d) ? d.ToString() : "(missing)";
            return (kvnr, dob);
        }
        return null;
    }

    private async Task<List<PatientRef>> QueryAsync(string filter, CancellationToken ct)
    {
        var listId = await PatientsListIdAsync(ct);
        string? next = $"{GraphBase}/sites/{_opt.SiteId}/lists/{listId}/items" +
                       $"?$expand=fields($select={FieldSelect})&$filter={Uri.EscapeDataString(filter)}&$top=200";
        var result = new List<PatientRef>();
        // Follow @odata.nextLink so a common surname (>1 page) is never silently truncated before the DOB filter.
        for (var page = 0; next is not null && page < 50; page++) // page cap = runaway guard
        {
            var root = await GetAsync(next, ct);
            foreach (var item in root.GetProperty("value").EnumerateArray())
                result.Add(GraphMapping.MapPatient(item));
            next = root.TryGetProperty("@odata.nextLink", out var nl) && nl.ValueKind == JsonValueKind.String ? nl.GetString() : null;
        }
        return result;
    }

    /// <summary>Diagnostics only: the PatientStatus choice values (column metadata, no patient data).</summary>
    public async Task<string[]> PatientStatusChoicesAsync(CancellationToken ct = default)
    {
        var listId = await PatientsListIdAsync(ct);
        var root = await GetAsync($"{GraphBase}/sites/{_opt.SiteId}/lists/{listId}/columns?$select=name,choice", ct);
        foreach (var col in root.GetProperty("value").EnumerateArray())
            if (GraphMapping.Text(col, "name") == "PatientStatus" && col.TryGetProperty("choice", out var ch) && ch.TryGetProperty("choices", out var arr))
                return arr.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        return Array.Empty<string>();
    }

    private async Task<string> PatientsListIdAsync(CancellationToken ct)
    {
        if (_patientsListId is not null) return _patientsListId;
        var root = await GetAsync($"{GraphBase}/sites/{_opt.SiteId}/lists?$filter=displayName eq 'Patients'&$select=id,displayName", ct);
        var first = root.GetProperty("value").EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Patients list not found on site.");
        _patientsListId = first.GetProperty("id").GetString()!;
        _log.LogDebug("Resolved 'Patients' list id");
        return _patientsListId;
    }

    // Graph's per-request server correlation id ('request-id'), falling back to the client id — PHI-free, the key that
    // ties an Adelaide failure to Microsoft's server-side logs.
    internal static string? GraphReqId(HttpResponseMessage resp) =>
        (resp.Headers.TryGetValues("request-id", out var v) ? v.FirstOrDefault() : null)
        ?? (resp.Headers.TryGetValues("client-request-id", out var c) ? c.FirstOrDefault() : null);

    private async Task<JsonElement> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var token = await _cred.GetTokenAsync(new TokenRequestContext(GraphScope), ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        // Status + timing only: the request URL carries the patient filter (KVNR/name = PHI) and the body can echo it, so
        // neither is logged. The body rides the thrown exception and is sanitised (Redact) at the catch points.
        if (!resp.IsSuccessStatusCode)
        {
            var reqId = GraphReqId(resp);
            _log.LogWarning("Graph patient query failed: HTTP {Status} in {ElapsedMs}ms graphReqId={GraphRequestId}", (int)resp.StatusCode, sw.ElapsedMilliseconds, reqId);
            throw Redact.HttpFail((int)resp.StatusCode, body, "Graph patient query", reqId);
        }
        _log.LogDebug("Graph patient query ok: HTTP {Status} in {ElapsedMs}ms", (int)resp.StatusCode, sw.ElapsedMilliseconds);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }
}

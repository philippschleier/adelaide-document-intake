using System.Text;
using System.Text.Json;

namespace Adelaide.Core;

/// <summary>PHI-safe rendering of values destined for logs, audit details, or user-facing errors.
///
/// Logs in this system must NEVER carry PHI. The sharp edges: canonical filenames embed the patient KVNR; Graph/HTTP
/// error bodies echo filter and filename values; transcripts are raw PHI. These helpers reduce such values to a short,
/// structural head that is safe to emit. The safe identifiers to log instead are the DocumentId and the patient
/// <em>list-item id</em> (both opaque, non-PHI), the content type, a SHA-256 prefix, and status codes.</summary>
public static class Redact
{
    private static readonly char[] BodyStart = { '{', '[', '\n', '\r' };

    /// <summary>An exception reduced to a short, PHI-safe head: the message up to the first brace/bracket/newline —
    /// where HTTP/Graph response bodies and JSON payloads begin — trimmed and capped. Use this everywhere an exception
    /// is rendered toward a sink; never emit <c>ex.Message</c> or <c>ex.ToString()</c> raw.</summary>
    public static string Error(Exception ex) => Error(ex.Message);

    /// <summary>Reduce an arbitrary message to its PHI-safe head (same rule as <see cref="Error(Exception)"/>).</summary>
    public static string Error(string? message)
    {
        var m = message ?? "";
        var cut = m.IndexOfAny(BodyStart);
        if (cut == 0) return "(body omitted)"; // the whole value IS a JSON/HTTP body — emitting it could leak PHI
        if (cut > 0) m = m[..cut];
        m = Scrub(m).Trim().TrimEnd(':'); // FINDING 7: drop control chars + bidi overrides, then cap harder (160 → 120)
        return m.Length > 120 ? m[..120] : m;
    }

    /// <summary>Remove characters that must never reach a log line or a user-facing message: C0/C1 control characters
    /// (incl. CR/LF/TAB — a log head is one line) and the Unicode BIDIRECTIONAL overrides/isolates (U+202A–U+202E,
    /// U+2066–U+2069) that can visually reorder surrounding text to spoof a value or forge a log entry (FINDING 7).</summary>
    public static string Scrub(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsControl(ch)) continue;                          // C0 (incl. CR/LF/TAB) and C1 controls
            int code = ch;
            if (code is >= 0x202A and <= 0x202E) continue;             // LRE RLE PDF LRO RLO
            if (code is >= 0x2066 and <= 0x2069) continue;             // LRI RLI FSI PDI
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>A PHI-safe message for a FAILED HTTP/Graph/OpenAI call: the operation + status + the response's non-PHI
    /// error CODE (parsed from <c>$.error.code</c>/<c>$.error.type</c> when present) — NEVER the raw body, which echoes
    /// filenames (the KVNR), patient filters, and prompt/response content. Use it when CONSTRUCTING the exception for a
    /// non-2xx response, so the body can never enter the exception message (and thus never a log @x field, the audit, or
    /// an HTTP response). Closes the body-leak path whether the body is JSON or delimiter-free text.</summary>
    public static string HttpError(int status, string? body, string op)
    {
        var code = TryErrorCode(body);
        return code is null ? $"{op} {status}" : $"{op} {status} {code}";
    }

    private static string? TryErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                foreach (var key in new[] { "code", "type" })
                    if (err.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                        return Scrub(s.Length > 40 ? s[..40] : s); // a code is a short machine token; cap + scrub as a guard against free text
        }
        catch { /* not JSON → status only, never the raw text */ }
        return null;
    }

    /// <summary>First 12 hex chars of a content hash — a non-PHI handle for a document's bytes, safe to log and to
    /// correlate against the audit ledger. Returns "(none)" when no hash is available.</summary>
    public static string Sha(string? sha256) =>
        string.IsNullOrWhiteSpace(sha256) ? "(none)" : sha256.Length <= 12 ? sha256 : sha256[..12];

    /// <summary>Builds the PHI-safe HTTP/Graph-failure exception (message via <see cref="HttpError"/>, body never
    /// retained) AND stashes the STRUCTURAL diagnostics in <see cref="Exception.Data"/>: <c>httpStatus</c> (int) and,
    /// for Graph, <c>graphRequestId</c> (the server correlation id). A downstream catch that logs the exception TYPE
    /// only (per the no-raw-ex rule — a message can embed a path/body, a type name cannot) can still read these keys
    /// and emit them as structured fields, so the status/request-id survive without the body ever reaching a sink.</summary>
    public static InvalidOperationException HttpFail(int status, string? body, string op, string? requestId = null)
    {
        var ex = new InvalidOperationException(HttpError(status, body, op));
        ex.Data["httpStatus"] = status;
        if (!string.IsNullOrEmpty(requestId)) ex.Data["graphRequestId"] = requestId;
        return ex;
    }

    /// <summary>The exception TYPE chain (outer ← inner ← …), type names ONLY — always PHI-safe (a message can embed a
    /// path/filename/body; a type name cannot). Pair with the outer <c>ExType</c> for filtering. Capped so a deep chain
    /// cannot bloat a log line. Use INSTEAD of passing the raw exception where it could carry a path/content.</summary>
    public static string Chain(Exception? ex)
    {
        var sb = new System.Text.StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append(" <- ");
            sb.Append(e.GetType().Name);
            if (sb.Length >= 180) { sb.Append(" <- …"); break; }
        }
        return sb.ToString();
    }
}

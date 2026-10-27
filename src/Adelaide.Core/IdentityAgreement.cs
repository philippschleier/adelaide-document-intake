namespace Adelaide.Core;

/// <summary>The extraction-accuracy ORACLE: what the model read for an identity field vs what the patient record
/// actually says, for the patient the document was matched to.
///
/// This is free, deterministic ground truth. Both halves are already held at review time — the model's value on the
/// review sidecar's fields[], the patient's value on match.candidate — so no new store, no new consent question, and
/// no human judgement is involved. It was previously computed and then thrown away: the reviewer's form overwrites
/// the identity fields from the patient record (Adelaide.Web.Client review.ts applyPatient), and the resulting
/// difference was recorded as `correctedFields`, i.e. as though a human had read the page and corrected the model.
/// It is not a correction. Selecting a patient is a MATCH.
///
/// Measured on the 2026-08-05 corpus (201 documents with a matched candidate): KVNR agreed 59% of the time,
/// LastName 98%, FirstName 97%, BirthDate 100%. So the identity-field "correction rate" that ranked KVNR as the
/// worst-extracted field was mostly this overwrite — but underneath it there IS a real KVNR precision problem, and
/// it is invisible while the two are conflated.
///
/// PHI: emits a field name, a boolean, and a STRUCTURAL descriptor of the difference — lengths and character
/// classes, never characters. "len 9-&gt;10" says the model dropped a character; "a-&gt;a@0" says the leading letter
/// was misread (the AOK 'O'/'0' family). Neither reveals the value.</summary>
public static class IdentityAgreement
{
    /// <summary>The identity fields the reviewer's patient selection overwrites, and therefore the ones whose
    /// "corrections" are really match artefacts. Ordinal-ignore-case, matching the extractor's column names.</summary>
    public static readonly IReadOnlySet<string> Fields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "KVNR", "LastName", "FirstName", "BirthDate" };

    public static bool IsIdentityField(string? column) => column is not null && Fields.Contains(column);

    /// <summary>Outcome of comparing one model-extracted value against the patient record's value.</summary>
    /// <param name="Field">Column name (non-PHI).</param>
    /// <param name="Agreed">True when the two match after trim/case normalisation.</param>
    /// <param name="Shape">Structural descriptor of the disagreement, or null when they agreed. Never a value.</param>
    public sealed record Result(string Field, bool Agreed, string? Shape);

    private static string Norm(string? s) => (s ?? "").Trim().ToUpperInvariant();

    /// <summary>Compare one field. Returns null when there is nothing to compare — the model read nothing (a RECALL
    /// gap, counted elsewhere) or the record holds nothing. Only a real value-vs-value pair is an oracle sample.</summary>
    public static Result? Compare(string field, string? modelValue, string? recordValue)
    {
        var a = Norm(modelValue);
        var b = Norm(recordValue);
        if (a.Length == 0 || b.Length == 0) return null;
        return a == b ? new Result(field, true, null) : new Result(field, false, Describe(a, b));
    }

    /// <summary>Structural description of how two normalised values differ. Length change when the lengths differ;
    /// otherwise the character CLASSES that moved and at which positions (d=digit, a=letter, x=other), capped so a
    /// wholesale mismatch does not turn into a long positional trace.</summary>
    private static string Describe(string a, string b)
    {
        if (a.Length != b.Length) return $"len {a.Length}->{b.Length}";
        var diffs = new List<string>();
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) diffs.Add($"{Class(a[i])}->{Class(b[i])}@{i}");
        return diffs.Count switch
        {
            0 => "equal",                                   // unreachable: callers compare for equality first
            <= 3 => string.Join(",", diffs),
            _ => $"{diffs.Count} positions",
        };
    }

    private static char Class(char c) => char.IsAsciiDigit(c) ? 'd' : char.IsLetter(c) ? 'a' : 'x';
}

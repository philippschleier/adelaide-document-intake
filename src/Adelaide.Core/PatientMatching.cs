using System.Globalization;

namespace Adelaide.Core;

/// <summary>Pure resolution of candidate patient rows into a match outcome (testable, adapter-agnostic).</summary>
public static class PatientMatching
{
    /// <summary>0 hits → None; 1 → Single; &gt;1 → Single only if exactly one is active, else Multiple (→ review).</summary>
    public static PatientMatch Resolve(IReadOnlyList<PatientRef> hits, string method)
    {
        if (hits.Count == 0) return new PatientMatch(MatchOutcome.None, null, method);
        if (hits.Count == 1) return new PatientMatch(MatchOutcome.Single, hits[0], method);
        var active = hits.Where(IsActive).ToList();
        return active.Count == 1
            ? new PatientMatch(MatchOutcome.Single, active[0], method)
            // ambiguous → review, carrying the contenders (the active ones when several, else all) for the picker
            : new PatientMatch(MatchOutcome.Multiple, null, method, active.Count > 1 ? active : hits);
    }

    /// <summary>The matching STRATEGY, shared by every directory backend so they cannot drift: a check-digit-valid KVNR
    /// first; if it matches nobody (often a directory data-entry error) fall through to surname + exact date of birth.
    /// The backend supplies only the two lookups — <paramref name="byKvnr"/> (exact) and <paramref name="byLastName"/>
    /// (exact surname; DOB is compared here). An invalid KVNR is never looked up: an OCR misread becomes "no match →
    /// review", not a wrong patient.</summary>
    public static async Task<PatientMatch> MatchAsync(ExtractedData data,
        Func<string, CancellationToken, Task<IReadOnlyList<PatientRef>>> byKvnr,
        Func<string, CancellationToken, Task<IReadOnlyList<PatientRef>>> byLastName,
        CancellationToken ct = default)
    {
        var kvnr = data.Value("KVNR");
        if (!string.IsNullOrWhiteSpace(kvnr) && Kvnr.IsValid(kvnr))
        {
            var byK = Resolve(await byKvnr(kvnr!, ct), "kvnr");
            if (byK.Outcome != MatchOutcome.None) return byK; // a KVNR hit (or ambiguous → review) wins
            // A valid KVNR matching nobody is often a directory data-entry error — try name+DOB before giving up.
        }

        var last = data.Value("LastName");
        if (!string.IsNullOrWhiteSpace(last) &&
            DateOnly.TryParseExact(data.Value("BirthDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dob)) // NormDate guarantees ISO-or-absent; exact parse, never lenient
        {
            var byName = await byLastName(last!, ct);
            return Resolve(byName.Where(p => p.BirthDate == dob).ToList(), "name+dob");
        }

        return new PatientMatch(MatchOutcome.None, null, "none");
    }

    public static bool IsActive(PatientRef p) =>
        p.Status.StartsWith("Aktiv", StringComparison.OrdinalIgnoreCase) ||
        p.Status.Equals("Active", StringComparison.OrdinalIgnoreCase);
}

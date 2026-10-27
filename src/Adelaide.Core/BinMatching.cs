namespace Adelaide.Core;

/// <summary>A deletion we KNOW happened: one Superseded document and the instant its terminal event was appended.
/// That timestamp is the ONLY fact the ledger and the SharePoint Recycle Bin have in common — <c>recycleBinItem.id</c>
/// is a delete-TRANSACTION id with no documented driveItemId back-reference (SUPERSEDE-LIFECYCLE.md §6.2), and the
/// filed rendition's byte size is unknowable for the current specimens (all 14 predate the capture build), so the
/// size half of the originally-sketched correlation does not exist. PHI-free by construction: an opaque 12-hex
/// document id and a timestamp, nothing else.</summary>
public readonly record struct SupersedeDeletion(string DocumentId, DateTimeOffset SupersededAt);

/// <summary>One Recycle-Bin entry reduced to its two NON-IDENTIFYING fields: the delete-transaction id (opaque,
/// GUID-like) and when the delete landed. This type is the PHI boundary of the bin census — <c>name</c> (the
/// canonical filename: patient name + insurance number), <c>deletedFromLocation</c> and <c>deletedBy</c> are dropped
/// by the caller at parse time and never reach anything downstream of this record.</summary>
public readonly record struct BinDeletion(string BinItemId, DateTimeOffset DeletedAt);

/// <summary>A bin entry that fell inside the tolerance window, with its SIGNED offset from the supersede event.
/// Negative = the bin delete landed FIRST, which is the expected sign: the supersede path deletes the SharePoint
/// item and only then appends the terminal event.</summary>
public readonly record struct BinCandidate(BinDeletion Item, TimeSpan Offset)
{
    /// <summary>Unsigned distance from the supersede event — what the window is compared against.</summary>
    public TimeSpan Distance => Offset.Duration();
}

/// <summary>One document's correlation result: every bin entry inside the window, nearest first.</summary>
public sealed record BinMatch(SupersedeDeletion Deletion, IReadOnlyList<BinCandidate> Candidates)
{
    public bool Matched => Candidates.Count > 0;

    /// <summary>More than one bin entry sits inside the window, so the timestamp alone cannot single one out.
    /// Expected for the 2026-07-05 batch (five supersedes inside eleven minutes) — reported, never silently
    /// resolved: picking a winner would invent a correlation the data does not support.</summary>
    public bool Ambiguous => Candidates.Count > 1;

    public BinCandidate? Nearest => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>The PURE half of the Recycle-Bin census (the Graph half was an operator command, not published here):
/// correlate known supersede deletions against a bin listing by TIMESTAMP PROXIMITY, and compute when a bin entry
/// ages out. No I/O, no clock, no PHI — every input is an opaque id or an instant, which is exactly what makes it
/// testable without a tenant.</summary>
public static class BinMatching
{
    /// <summary>For each supersede deletion, every bin entry whose <c>deletedDateTime</c> lies within
    /// <paramref name="window"/> of the supersede event, ordered nearest-first (ties broken ordinally on the bin item
    /// id so the output is stable across runs). Results come back one-per-deletion in input order, including the
    /// unmatched ones — an absent match is a finding, not a row to drop. Deliberately NOT a bipartite assignment:
    /// a bin entry may appear as a candidate for several documents, and that overlap is the honest reading of a
    /// batch of deletions inside one window.</summary>
    public static IReadOnlyList<BinMatch> Correlate(
        IReadOnlyList<SupersedeDeletion> deletions, IReadOnlyList<BinDeletion> bin, TimeSpan window)
    {
        if (window < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window), "The tolerance window cannot be negative.");
        var matches = new List<BinMatch>(deletions.Count);
        foreach (var d in deletions)
        {
            var candidates = bin
                .Select(b => new BinCandidate(b, b.DeletedAt - d.SupersededAt))
                .Where(c => c.Distance <= window) // BOUNDARY INCLUSIVE: exactly ±window is a match
                .OrderBy(c => c.Distance)
                .ThenBy(c => c.Item.BinItemId, StringComparer.Ordinal)
                .ToList();
            matches.Add(new BinMatch(d, candidates));
        }
        return matches;
    }

    /// <summary>When a bin entry ages out: its deletion instant plus the retention period. The caller supplies the
    /// retention it MEASURED (or, explicitly labelled as such, the documented default) — this does not know one.</summary>
    public static DateTimeOffset Expiry(DateTimeOffset deletedAt, int retentionDays) =>
        retentionDays < 0
            ? throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention cannot be negative.")
            : deletedAt.AddDays(retentionDays);

    /// <summary>Whole days left before an expiry, floored; negative once it has passed. Floored rather than rounded
    /// because a partial day of remaining bin life must never read as a whole one on a time-boxed errand.</summary>
    public static int DaysUntil(DateTimeOffset expiry, DateTimeOffset now) => (int)Math.Floor((expiry - now).TotalDays);
}

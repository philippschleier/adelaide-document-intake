namespace Adelaide.Core;

/// <summary>What a changed field in a review diff actually TELLS us. Three different things were previously collapsed
/// into one "corrected" flag, which made the per-field correction ranking point at the wrong fields.
///
///   MATCH      — the reviewer selected a patient and the identity fields were populated from the patient record.
///                No human read the document to produce these values. NOT evidence about extraction quality; the
///                model-vs-record comparison is captured separately as <see cref="IdentityAgreement"/>.
///   CORRECTION — the model proposed a value and the human changed it. A PRECISION failure, and the only one of the
///                three that is ground truth about the model being WRONG.
///   FILL       — the model proposed nothing and the human supplied a value. A RECALL failure. Real signal, but a
///                different one: sharpening a prompt for a field the model never attempts is a different fix from
///                sharpening one it attempts and gets wrong.
///
/// On the 2026-08-05 ledger the four identity fields held the top four places in the correction ranking (KVNR 72%,
/// BirthDate 34%, LastName 24%, FirstName 19%) purely as MATCH artefacts.</summary>
public enum ReviewFieldSignal
{
    Match,
    Correction,
    Fill,
}

public static class ReviewFieldSignals
{
    /// <summary>Classify one changed field.
    ///
    /// The identity fields are MATCH by construction whenever a patient is linked: the review form overwrites them
    /// from the patient record on load AND on selection (review.ts applyPatient), so a human cannot express a
    /// correction to them through the UI at all — there is no interaction that produces a hand-typed identity value.
    /// Treating them as corrections would be reading a signal the interface cannot emit.
    ///
    /// With NO patient linked the overwrite never ran, so a changed identity field is a genuine human entry and falls
    /// through to the ordinary correction/fill split.</summary>
    /// <param name="column">The extracted column name.</param>
    /// <param name="modelProposedAValue">True when the model's extraction had a non-empty value for this column.</param>
    /// <param name="patientLinked">True when the submission carries a linked patient.</param>
    public static ReviewFieldSignal Classify(string column, bool modelProposedAValue, bool patientLinked)
    {
        if (patientLinked && IdentityAgreement.IsIdentityField(column)) return ReviewFieldSignal.Match;
        return modelProposedAValue ? ReviewFieldSignal.Correction : ReviewFieldSignal.Fill;
    }

    /// <summary>The camelCase tag written into the review diff.</summary>
    public static string Tag(this ReviewFieldSignal s) => s switch
    {
        ReviewFieldSignal.Match => "match",
        ReviewFieldSignal.Correction => "correction",
        ReviewFieldSignal.Fill => "fill",
        _ => "unknown",
    };
}

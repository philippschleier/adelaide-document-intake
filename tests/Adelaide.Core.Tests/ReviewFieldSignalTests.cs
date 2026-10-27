using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// Three things were previously collapsed into one "corrected" flag. Separating them is what stops the per-field
// ranking pointing at the wrong prompts: on the 2026-08-05 ledger the four identity fields held the top four places
// (KVNR 72%, BirthDate 34%, LastName 24%, FirstName 19%) purely because selecting a patient overwrites them.
public class ReviewFieldSignalTests
{
    [Fact]
    public void An_identity_field_with_a_linked_patient_is_a_MATCH_not_a_correction()
        => Assert.Equal(ReviewFieldSignal.Match,
            ReviewFieldSignals.Classify("KVNR", modelProposedAValue: true, patientLinked: true));

    // The crucial one: even though the model DID propose a KVNR and the submitted value differs, that difference is
    // the patient record overwriting it — not a human reading the page. Classifying it as a correction is what
    // inflated the ranking.
    [Fact]
    public void A_model_proposed_identity_value_is_still_a_MATCH_when_a_patient_is_linked()
    {
        foreach (var f in new[] { "KVNR", "LastName", "FirstName", "BirthDate" })
            Assert.Equal(ReviewFieldSignal.Match, ReviewFieldSignals.Classify(f, true, patientLinked: true));
    }

    // With NO patient linked the form never overwrote anything, so a changed identity field IS a human entry.
    [Fact]
    public void Without_a_linked_patient_an_identity_field_falls_through_to_the_normal_split()
    {
        Assert.Equal(ReviewFieldSignal.Correction, ReviewFieldSignals.Classify("KVNR", true, patientLinked: false));
        Assert.Equal(ReviewFieldSignal.Fill, ReviewFieldSignals.Classify("KVNR", false, patientLinked: false));
    }

    [Fact]
    public void A_changed_non_identity_field_the_model_proposed_is_a_CORRECTION()
        => Assert.Equal(ReviewFieldSignal.Correction,
            ReviewFieldSignals.Classify("DocumentDate", modelProposedAValue: true, patientLinked: true));

    [Fact]
    public void A_non_identity_field_the_model_left_empty_is_a_FILL()
        => Assert.Equal(ReviewFieldSignal.Fill,
            ReviewFieldSignals.Classify("DocumentDate", modelProposedAValue: false, patientLinked: true));

    // A linked patient must not reclassify ordinary extraction fields — the overwrite only touches the identity four.
    [Fact]
    public void A_linked_patient_does_not_affect_non_identity_fields()
        => Assert.Equal(ReviewFieldSignal.Correction,
            ReviewFieldSignals.Classify("ApprovalNumber", true, patientLinked: true));

    [Theory]
    [InlineData(ReviewFieldSignal.Match, "match")]
    [InlineData(ReviewFieldSignal.Correction, "correction")]
    [InlineData(ReviewFieldSignal.Fill, "fill")]
    public void Tags_are_the_stable_wire_names(ReviewFieldSignal s, string tag)
        => Assert.Equal(tag, s.Tag());
}

using Adelaide.Core;
using Xunit;

namespace Adelaide.Web.Tests;

// Unit tests for the PURE supersede guard (SupersedeGuard.Validate) — the endpoint-level half of the supersede
// invariants, exercised without hosting /api/review. Covers every Result outcome, including the two "rejected" cases
// the ruling calls out: supersedeTarget ≠ duplicateOf, a non-business-key source (DuplicateSuspect), a stale/non-Filed
// target, and a legacy target with no driveItemId.
public class SupersedeGuardTests
{
    private static DocumentAudit Doc(string id, string state, string? driveItemId) =>
        new(id, state, null, null, null, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, DriveItemId: driveItemId);

    [Fact]
    public void No_supersedeTarget_is_NotSupersede() => // a plain (non-supersede) review submit
        Assert.Equal(SupersedeGuard.Result.NotSupersede, SupersedeGuard.Validate(null, "old1", Doc("old1", "Filed", "drv1")));

    [Fact]
    public void Target_must_equal_the_source_duplicateOf() => // ≠ duplicateOf ⇒ rejected (never a general delete lever)
        Assert.Equal(SupersedeGuard.Result.MismatchDuplicate, SupersedeGuard.Validate("other", "old1", Doc("other", "Filed", "drv1")));

    [Fact]
    public void Source_without_a_duplicateOf_is_rejected() => // DuplicateSuspect / plain NeedsReview: null duplicateOf ⇒ mismatch
        Assert.Equal(SupersedeGuard.Result.MismatchDuplicate, SupersedeGuard.Validate("old1", null, Doc("old1", "Filed", "drv1")));

    [Fact]
    public void Target_not_found_is_TargetNotFiled() =>
        Assert.Equal(SupersedeGuard.Result.TargetNotFiled, SupersedeGuard.Validate("old1", "old1", null));

    [Fact]
    public void Target_not_Filed_is_TargetNotFiled() => // discarded / already superseded / never filed
        Assert.Equal(SupersedeGuard.Result.TargetNotFiled, SupersedeGuard.Validate("old1", "old1", Doc("old1", "NeedsReview", "drv1")));

    [Fact]
    public void Filed_target_without_a_locator_is_TargetNoLocator() => // legacy pre-backfill ⇒ id-only delete impossible
        Assert.Equal(SupersedeGuard.Result.TargetNoLocator, SupersedeGuard.Validate("old1", "old1", Doc("old1", "Filed", null)));

    [Fact]
    public void Filed_target_with_locator_matching_duplicateOf_is_Ok() =>
        Assert.Equal(SupersedeGuard.Result.Ok, SupersedeGuard.Validate("old1", "old1", Doc("old1", "Filed", "drv1")));
}

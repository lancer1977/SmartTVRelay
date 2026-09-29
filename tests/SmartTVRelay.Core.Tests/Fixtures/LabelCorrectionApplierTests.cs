namespace SmartTVRelay.Core.Tests.Fixtures;

using SmartTVRelay.Core;
using SmartTVRelay.Core.Fixtures;
using Xunit;

public class LabelCorrectionApplierTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static readonly ExpectedSegment Program0To30 = new(S(0), S(30), BroadcastState.Program);
    private static readonly ExpectedSegment Commercial30To90 = new(S(30), S(90), BroadcastState.Commercial);
    private static readonly ExpectedSegment Program90To120 = new(S(90), S(120), BroadcastState.Program);

    private static List<ExpectedSegment> Baseline() => [Program0To30, Commercial30To90, Program90To120];

    [Fact]
    public void Apply_AddIntoAGap_InsertsInSortedOrder()
    {
        var baseline = new List<ExpectedSegment> { Program0To30, Program90To120 };
        var addition = new ExpectedSegment(S(30), S(90), BroadcastState.Commercial);
        var correction = LabelCorrection.CreateAdd(Guid.NewGuid(), "alice", addition);

        var result = LabelCorrectionApplier.Apply(baseline, [correction]);

        Assert.Empty(result.Conflicts);
        Assert.Equal([Program0To30, addition, Program90To120], result.ResultingTimeline);
    }

    [Fact]
    public void Apply_AddOverlappingExistingSegment_IsAConflictAndLeavesTimelineUnchanged()
    {
        var baseline = Baseline();
        var overlapping = new ExpectedSegment(S(20), S(40), BroadcastState.Commercial);
        var correction = LabelCorrection.CreateAdd(Guid.NewGuid(), "alice", overlapping);

        var result = LabelCorrectionApplier.Apply(baseline, [correction]);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Same(correction, conflict.Correction);
        Assert.Equal(baseline, result.ResultingTimeline);
    }

    [Fact]
    public void Apply_ChangeExistingSegmentBoundary_Succeeds()
    {
        var baseline = Baseline();
        var revised = new ExpectedSegment(S(35), S(90), BroadcastState.Commercial);
        var correction = LabelCorrection.CreateChange(Guid.NewGuid(), "alice", Commercial30To90, revised);

        var result = LabelCorrectionApplier.Apply(baseline, [correction]);

        Assert.Empty(result.Conflicts);
        Assert.Equal([Program0To30, revised, Program90To120], result.ResultingTimeline);
    }

    [Fact]
    public void Apply_ChangeTargetNoLongerPresent_IsAConflict()
    {
        var baseline = Baseline();
        var alreadyMoved = new ExpectedSegment(S(30), S(80), BroadcastState.Commercial);
        var correction = LabelCorrection.CreateChange(Guid.NewGuid(), "alice", alreadyMoved, new ExpectedSegment(S(30), S(85), BroadcastState.Commercial));

        var result = LabelCorrectionApplier.Apply(baseline, [correction]);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Same(correction, conflict.Correction);
        Assert.Equal(baseline, result.ResultingTimeline);
    }

    [Fact]
    public void Apply_ChangeReplacementWouldOverlapAnotherSegment_IsAConflictAndOriginalTargetIsPreserved()
    {
        var baseline = Baseline();
        // Extending the middle segment into the following segment's range must not partially
        // apply -- the original target must survive untouched, not be removed and left dangling.
        var overreaching = new ExpectedSegment(S(30), S(100), BroadcastState.Commercial);
        var correction = LabelCorrection.CreateChange(Guid.NewGuid(), "alice", Commercial30To90, overreaching);

        var result = LabelCorrectionApplier.Apply(baseline, [correction]);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Same(correction, conflict.Correction);
        Assert.Equal(baseline, result.ResultingTimeline);
        Assert.Contains(Commercial30To90, result.ResultingTimeline);
    }

    [Fact]
    public void Apply_RemoveExistingSegment_Succeeds()
    {
        var baseline = Baseline();
        var correction = LabelCorrection.CreateRemove(Guid.NewGuid(), "alice", Commercial30To90);

        var result = LabelCorrectionApplier.Apply(baseline, [correction]);

        Assert.Empty(result.Conflicts);
        Assert.Equal([Program0To30, Program90To120], result.ResultingTimeline);
    }

    [Fact]
    public void Apply_RemoveTargetNotPresent_IsAConflict()
    {
        var baseline = Baseline();
        var neverExisted = new ExpectedSegment(S(200), S(210), BroadcastState.Commercial);
        var correction = LabelCorrection.CreateRemove(Guid.NewGuid(), "alice", neverExisted);

        var result = LabelCorrectionApplier.Apply(baseline, [correction]);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Same(correction, conflict.Correction);
        Assert.Equal(baseline, result.ResultingTimeline);
    }

    [Fact]
    public void Apply_SequentialCorrectionsWhereSecondDependsOnFirst_AppliesInListOrder()
    {
        var baseline = Baseline();
        var revised = new ExpectedSegment(S(35), S(90), BroadcastState.Commercial);
        var change = LabelCorrection.CreateChange(Guid.NewGuid(), "alice", Commercial30To90, revised);
        var removeRevised = LabelCorrection.CreateRemove(Guid.NewGuid(), "bob", revised);

        var result = LabelCorrectionApplier.Apply(baseline, [change, removeRevised]);

        Assert.Empty(result.Conflicts);
        Assert.Equal([Program0To30, Program90To120], result.ResultingTimeline);
    }

    [Fact]
    public void Apply_SameCorrectionIdAppliedTwiceInOneReplay_IsIdempotent()
    {
        var baseline = new List<ExpectedSegment> { Program0To30, Program90To120 };
        var addition = new ExpectedSegment(S(30), S(90), BroadcastState.Commercial);
        var correction = LabelCorrection.CreateAdd(Guid.NewGuid(), "alice", addition);

        var result = LabelCorrectionApplier.Apply(baseline, [correction, correction]);

        // The second occurrence of the same Id must be a silent no-op, not a second Add attempt
        // that would otherwise conflict with the first (overlapping itself).
        Assert.Empty(result.Conflicts);
        Assert.Equal([Program0To30, addition, Program90To120], result.ResultingTimeline);
    }

    [Fact]
    public void Apply_ReplayingTheSameCorrectionLogTwice_ProducesTheSameResult()
    {
        var baseline = Baseline();
        var revised = new ExpectedSegment(S(35), S(90), BroadcastState.Commercial);
        var corrections = new List<LabelCorrection>
        {
            LabelCorrection.CreateChange(Guid.NewGuid(), "alice", Commercial30To90, revised),
        };

        var first = LabelCorrectionApplier.Apply(baseline, corrections);
        var second = LabelCorrectionApplier.Apply(baseline, corrections);

        Assert.Equal(first.ResultingTimeline, second.ResultingTimeline);
        Assert.Equal(first.Conflicts, second.Conflicts);
    }

    [Fact]
    public void Apply_ProvenanceAndCommentAreCarriedThroughUnmodified()
    {
        var correction = LabelCorrection.CreateAdd(
            Guid.NewGuid(), "alice", new ExpectedSegment(S(0), S(10), BroadcastState.Program), comment: "manual fix from broadcast log");

        Assert.Equal("alice", correction.Author);
        Assert.Equal("manual fix from broadcast log", correction.Comment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateAdd_BlankAuthor_Throws(string author)
    {
        Assert.Throws<ArgumentException>(() => LabelCorrection.CreateAdd(Guid.NewGuid(), author, Program0To30));
    }

    [Fact]
    public void Apply_EmptyCorrectionList_ReturnsBaselineUnchanged()
    {
        var baseline = Baseline();

        var result = LabelCorrectionApplier.Apply(baseline, []);

        Assert.Empty(result.Conflicts);
        Assert.Equal(baseline, result.ResultingTimeline);
    }
}

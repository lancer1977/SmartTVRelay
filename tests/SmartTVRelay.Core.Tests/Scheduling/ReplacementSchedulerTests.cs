namespace SmartTVRelay.Core.Tests.Scheduling;

using SmartTVRelay.Core.Catalog;
using SmartTVRelay.Core.Scheduling;
using Xunit;

public class ReplacementSchedulerTests
{
    private static ReplacementClipEntry Clip(string id, double seconds, bool enabled = true) =>
        new(id, $"/clips/{id}.mp4", TimeSpan.FromSeconds(seconds), new CodecCompatibilitySummary("mp4", "h264", "aac"), [], enabled);

    [Fact]
    public void Schedule_EmptyCandidates_ReturnsNoMedia()
    {
        var scheduler = new ReplacementScheduler();

        var result = scheduler.Schedule(TimeSpan.FromSeconds(30), []);

        Assert.Equal(SchedulingOutcome.NoMedia, result.Outcome);
        Assert.Empty(result.SelectedClips);
        Assert.Equal(TimeSpan.Zero, result.TotalDuration);
    }

    [Fact]
    public void Schedule_AllCandidatesDisabled_ReturnsNoMedia()
    {
        var scheduler = new ReplacementScheduler();
        var candidates = new[] { Clip("a", 30, enabled: false) };

        var result = scheduler.Schedule(TimeSpan.FromSeconds(30), candidates);

        Assert.Equal(SchedulingOutcome.NoMedia, result.Outcome);
    }

    [Fact]
    public void Schedule_SmallestClipExceedsTolerance_ReturnsNoFit()
    {
        var scheduler = new ReplacementScheduler();
        var candidates = new[] { Clip("a", 120) };

        var result = scheduler.Schedule(TimeSpan.FromSeconds(15), candidates, tolerance: TimeSpan.FromSeconds(1));

        Assert.Equal(SchedulingOutcome.NoFit, result.Outcome);
        Assert.Empty(result.SelectedClips);
    }

    [Fact]
    public void Schedule_SingleExactMatch_SelectsThatClip()
    {
        var scheduler = new ReplacementScheduler();
        var candidates = new[] { Clip("short", 10), Clip("exact", 30), Clip("long", 60) };

        var result = scheduler.Schedule(TimeSpan.FromSeconds(30), candidates, tolerance: TimeSpan.FromSeconds(1));

        Assert.Equal(SchedulingOutcome.Fit, result.Outcome);
        Assert.Equal(["exact"], result.SelectedClips.Select(c => c.Id));
        Assert.Equal(TimeSpan.FromSeconds(30), result.TotalDuration);
    }

    [Fact]
    public void Schedule_NoSingleClipFits_CombinesMultipleClips()
    {
        var scheduler = new ReplacementScheduler();
        // Common commercial-break durations: two 15s clips filling a 30s break.
        var candidates = new[] { Clip("a", 15), Clip("b", 15), Clip("c", 8) };

        var result = scheduler.Schedule(TimeSpan.FromSeconds(30), candidates, tolerance: TimeSpan.FromSeconds(1));

        Assert.Equal(SchedulingOutcome.Fit, result.Outcome);
        Assert.Equal(2, result.SelectedClips.Count);
        Assert.Equal(TimeSpan.FromSeconds(30), result.TotalDuration);
    }

    [Fact]
    public void Schedule_TotalNeverExceedsTargetPlusTolerance()
    {
        var scheduler = new ReplacementScheduler();
        var candidates = new[] { Clip("a", 20), Clip("b", 20), Clip("c", 20) };

        var result = scheduler.Schedule(TimeSpan.FromSeconds(30), candidates, tolerance: TimeSpan.FromSeconds(2));

        Assert.True(result.TotalDuration <= TimeSpan.FromSeconds(32));
    }

    [Fact]
    public void Schedule_ExactlyAtTolerantBoundary_IsAccepted()
    {
        var scheduler = new ReplacementScheduler();
        var candidates = new[] { Clip("a", 31) };

        var result = scheduler.Schedule(TimeSpan.FromSeconds(30), candidates, tolerance: TimeSpan.FromSeconds(1));

        Assert.Equal(SchedulingOutcome.Fit, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(31), result.TotalDuration);
    }

    [Fact]
    public void Schedule_JustOverTolerantBoundary_IsRejected()
    {
        var scheduler = new ReplacementScheduler();
        var candidates = new[] { Clip("a", 31.5) };

        var result = scheduler.Schedule(TimeSpan.FromSeconds(30), candidates, tolerance: TimeSpan.FromSeconds(1));

        Assert.Equal(SchedulingOutcome.NoFit, result.Outcome);
    }

    [Fact]
    public void Schedule_SameSeed_ProducesSameSelectionAcrossCalls()
    {
        var scheduler = new ReplacementScheduler();
        var candidates = new[] { Clip("a", 15), Clip("b", 15), Clip("c", 15), Clip("d", 15) };

        var first = scheduler.Schedule(TimeSpan.FromSeconds(30), candidates, tolerance: TimeSpan.FromSeconds(1), seed: 42);
        var second = scheduler.Schedule(TimeSpan.FromSeconds(30), candidates, tolerance: TimeSpan.FromSeconds(1), seed: 42);

        Assert.Equal(first.SelectedClips.Select(c => c.Id), second.SelectedClips.Select(c => c.Id));
    }

    [Fact]
    public void Schedule_OmittedSeed_IsDeterministicAcrossCalls()
    {
        var scheduler = new ReplacementScheduler();
        var candidates = new[] { Clip("a", 15), Clip("b", 15), Clip("c", 15), Clip("d", 15) };

        var first = scheduler.Schedule(TimeSpan.FromSeconds(30), candidates, tolerance: TimeSpan.FromSeconds(1));
        var second = scheduler.Schedule(TimeSpan.FromSeconds(30), candidates, tolerance: TimeSpan.FromSeconds(1));

        Assert.Equal(first.SelectedClips.Select(c => c.Id), second.SelectedClips.Select(c => c.Id));
    }

    [Fact]
    public void Schedule_DifferentSeeds_CanProduceDifferentTieBreaks()
    {
        var scheduler = new ReplacementScheduler();
        var candidates = Enumerable.Range(0, 10).Select(i => Clip($"clip{i}", 15)).ToArray();

        var selections = Enumerable.Range(0, 10)
            .Select(seed => scheduler.Schedule(TimeSpan.FromSeconds(30), candidates, tolerance: TimeSpan.FromSeconds(1), seed: seed))
            .Select(r => string.Join(",", r.SelectedClips.Select(c => c.Id)))
            .Distinct()
            .ToList();

        // Not every seed need differ, but across 10 seeds and 10 equally-eligible clips we should
        // see more than one distinct selection -- otherwise the seed isn't influencing anything.
        Assert.True(selections.Count > 1, "Expected varying seeds to produce more than one distinct clip selection.");
    }

    [Fact]
    public void Schedule_DisabledClipsAreIgnoredEvenWhenBetterFit()
    {
        var scheduler = new ReplacementScheduler();
        var candidates = new[] { Clip("perfect", 30, enabled: false), Clip("okay", 29) };

        var result = scheduler.Schedule(TimeSpan.FromSeconds(30), candidates, tolerance: TimeSpan.FromSeconds(2));

        Assert.Equal(["okay"], result.SelectedClips.Select(c => c.Id));
    }
}

namespace SmartTVRelay.Core.Tests.Relay;

using SmartTVRelay.Core;
using SmartTVRelay.Core.Relay;
using Xunit;

public class RelaySwitchingStateMachineTests
{
    [Fact]
    public void InitialState_IsOriginal()
    {
        var sm = new RelaySwitchingStateMachine();

        Assert.Equal(RelayState.Original, sm.State);
    }

    [Fact]
    public void Constructor_NonPositiveConfirmations_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RelaySwitchingStateMachine(0));
    }

    [Fact]
    public void Original_PreserveOriginal_StaysOriginal()
    {
        var sm = new RelaySwitchingStateMachine();

        var result = sm.Advance(ReplacementDecision.PreserveOriginal);

        Assert.Equal(RelayState.Original, result);
    }

    [Fact]
    public void Original_SingleAuthorize_EntersEnteringReplacementNotReplacementYet()
    {
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 2);

        var result = sm.Advance(ReplacementDecision.AuthorizeReplacement);

        Assert.Equal(RelayState.EnteringReplacement, result);
    }

    [Fact]
    public void EnteringReplacement_NotEnoughConfirmationsYet_StaysInEnteringReplacement()
    {
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 3);
        sm.Advance(ReplacementDecision.AuthorizeReplacement); // 1 of 3 -> EnteringReplacement

        var result = sm.Advance(ReplacementDecision.AuthorizeReplacement); // 2 of 3 -- not enough yet

        Assert.Equal(RelayState.EnteringReplacement, result);
    }

    [Fact]
    public void EnteringReplacement_EnoughConsecutiveAuthorizes_CommitsToReplacement()
    {
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 2);
        sm.Advance(ReplacementDecision.AuthorizeReplacement);

        var result = sm.Advance(ReplacementDecision.AuthorizeReplacement);

        Assert.Equal(RelayState.Replacement, result);
    }

    [Fact]
    public void EnteringReplacement_SinglePreserveOriginal_AbortsImmediatelyToOriginal()
    {
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 3);
        sm.Advance(ReplacementDecision.AuthorizeReplacement);

        var result = sm.Advance(ReplacementDecision.PreserveOriginal);

        Assert.Equal(RelayState.Original, result);
    }

    [Fact]
    public void ConfirmationsRequiredOfOne_SingleAuthorize_CommitsImmediately()
    {
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 1);

        var result = sm.Advance(ReplacementDecision.AuthorizeReplacement);

        Assert.Equal(RelayState.Replacement, result);
    }

    [Fact]
    public void Replacement_ContinuedAuthorize_StaysInReplacement()
    {
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 1);
        sm.Advance(ReplacementDecision.AuthorizeReplacement);

        var result = sm.Advance(ReplacementDecision.AuthorizeReplacement);

        Assert.Equal(RelayState.Replacement, result);
    }

    [Fact]
    public void Replacement_PreserveOriginal_MovesToReturningToOriginalNotDirectlyOriginal()
    {
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 1);
        sm.Advance(ReplacementDecision.AuthorizeReplacement);

        var result = sm.Advance(ReplacementDecision.PreserveOriginal);

        Assert.Equal(RelayState.ReturningToOriginal, result);
    }

    [Fact]
    public void ReturningToOriginal_FurtherPreserveOriginal_CompletesReturnToOriginal()
    {
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 1);
        sm.Advance(ReplacementDecision.AuthorizeReplacement);
        sm.Advance(ReplacementDecision.PreserveOriginal);

        var result = sm.Advance(ReplacementDecision.PreserveOriginal);

        Assert.Equal(RelayState.Original, result);
    }

    [Fact]
    public void ReturningToOriginal_AuthorizeAgain_GoesStraightBackToReplacement()
    {
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 1);
        sm.Advance(ReplacementDecision.AuthorizeReplacement);
        sm.Advance(ReplacementDecision.PreserveOriginal);

        var result = sm.Advance(ReplacementDecision.AuthorizeReplacement);

        Assert.Equal(RelayState.Replacement, result);
    }

    [Fact]
    public void ReturnToOriginal_IsFasterThanEnteringReplacement()
    {
        // With a high confirmation requirement, entering replacement takes many ticks, but
        // leaving it from Replacement never takes more than two ticks regardless of that
        // requirement -- demonstrating the asymmetric hysteresis the issue calls for.
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 5);
        for (var i = 0; i < 5; i++)
        {
            sm.Advance(ReplacementDecision.AuthorizeReplacement);
        }

        Assert.Equal(RelayState.Replacement, sm.State);

        sm.Advance(ReplacementDecision.PreserveOriginal);
        var result = sm.Advance(ReplacementDecision.PreserveOriginal);

        Assert.Equal(RelayState.Original, result);
    }

    [Theory]
    [InlineData(RelayState.Original)]
    [InlineData(RelayState.EnteringReplacement)]
    [InlineData(RelayState.Replacement)]
    [InlineData(RelayState.ReturningToOriginal)]
    public void ReportFault_ForcesFailOpenFromAnyState(RelayState startingState)
    {
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 2);
        DriveToState(sm, startingState);
        Assert.Equal(startingState, sm.State); // sanity-check the helper actually reached it

        var result = sm.ReportFault();

        Assert.Equal(RelayState.FailOpen, result);
    }

    [Fact]
    public void FailOpen_DecisionsHaveNoEffect()
    {
        var sm = new RelaySwitchingStateMachine();
        sm.ReportFault();

        sm.Advance(ReplacementDecision.AuthorizeReplacement);

        Assert.Equal(RelayState.FailOpen, sm.State);
    }

    [Fact]
    public void ClearFault_ReturnsToOriginal()
    {
        var sm = new RelaySwitchingStateMachine();
        sm.ReportFault();

        var result = sm.ClearFault();

        Assert.Equal(RelayState.Original, result);
    }

    [Fact]
    public void ClearFault_WhenNotFaulted_IsNoOp()
    {
        var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 1);
        sm.Advance(ReplacementDecision.AuthorizeReplacement);

        var result = sm.ClearFault();

        Assert.Equal(RelayState.Replacement, result);
    }

    [Fact]
    public void DeterministicTransitions_SameInputSequence_SameFinalState()
    {
        ReplacementDecision[] sequence =
        [
            ReplacementDecision.AuthorizeReplacement,
            ReplacementDecision.AuthorizeReplacement,
            ReplacementDecision.PreserveOriginal,
            ReplacementDecision.AuthorizeReplacement,
            ReplacementDecision.PreserveOriginal,
            ReplacementDecision.PreserveOriginal,
        ];

        RelayState RunSequence()
        {
            var sm = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 2);
            RelayState last = sm.State;
            foreach (var decision in sequence)
            {
                last = sm.Advance(decision);
            }

            return last;
        }

        var first = RunSequence();
        var second = RunSequence();

        Assert.Equal(first, second);
    }

    /// <summary>Drives <paramref name="sm"/> to <paramref name="target"/>, assuming it was
    /// constructed with confirmationsRequiredToEnterReplacement: 2.</summary>
    private static void DriveToState(RelaySwitchingStateMachine sm, RelayState target)
    {
        switch (target)
        {
            case RelayState.Original:
                break;
            case RelayState.EnteringReplacement:
                sm.Advance(ReplacementDecision.AuthorizeReplacement); // 1 of 2 -> EnteringReplacement
                break;
            case RelayState.Replacement:
                sm.Advance(ReplacementDecision.AuthorizeReplacement);
                sm.Advance(ReplacementDecision.AuthorizeReplacement); // 2 of 2 -> Replacement
                break;
            case RelayState.ReturningToOriginal:
                sm.Advance(ReplacementDecision.AuthorizeReplacement);
                sm.Advance(ReplacementDecision.AuthorizeReplacement); // -> Replacement
                sm.Advance(ReplacementDecision.PreserveOriginal); // -> ReturningToOriginal
                break;
            case RelayState.FailOpen:
                sm.ReportFault();
                break;
        }
    }
}

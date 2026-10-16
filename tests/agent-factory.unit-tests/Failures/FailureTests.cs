namespace AgentFactory.UnitTests.Failures;

using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Rounds;

/// <summary>
/// Classification and the retry predicate, at the layer where the rule is decided.
/// </summary>
/// <remarks>
/// The main matrix of both is already exercised in `RoundRetryTests`; the assertions here are the
/// ones that file leaves — the unclassified default across the exception types a caller is most
/// likely to be handed, and the round's own failure field as against the predicate.
/// </remarks>
public class FailureTests
{
    [Fact]
    public void A_failure_that_has_not_classified_itself_is_permanent()
    {
        // The load-bearing default, and it is deliberately conservative: an unclassified failure is
        // not retried, because the alternative is an unattended, unclassified retry of the one
        // operation a repository cannot take back — which is exactly what parking a failed merge
        // exists to prevent.
        foreach (var failure in new Exception[]
        {
            new Exception("something went wrong"),
            new InvalidOperationException("bad state"),
            new TimeoutException("the request timed out"),
            new TaskCanceledException("the caller cancelled"),
            new OperationCanceledException("cancelled"),
            new HttpRequestException("connection reset"),
        })
        {
            Assert.Equal(FailureClass.Permanent, Failures.Classify(failure));
        }

        Assert.Throws<ArgumentNullException>(() => Failures.Classify(null!));
    }

    [Fact]
    public void A_failure_is_read_as_the_component_that_threw_it_declared_it()
    {
        Assert.Equal(FailureClass.Transient, Failures.Classify(new TransientFailure("an image would not pull")));
        Assert.Equal(FailureClass.Permanent, Failures.Classify(new PermanentFailure("no such repository")));

        // A word in the message decides nothing: the direction is a property of the type thrown.
        Assert.Equal(FailureClass.Transient, Failures.Classify(new TransientFailure("permanent")));
        Assert.Equal(FailureClass.Permanent, Failures.Classify(new PermanentFailure("transient")));
    }

    [Fact]
    public void Only_a_failed_transient_round_is_retryable()
    {
        var transient = RoundResult.Failed(FailureClass.Transient);
        Assert.True(transient.IsRetryable);
        Assert.Equal(FailureClass.Transient, transient.Failure);
        Assert.Equal(RoundOutcome.Failed, transient.Outcome);

        Assert.False(RoundResult.Failed(FailureClass.Permanent).IsRetryable);

        // A round that ran to completion is not retryable whatever its commands returned: a build
        // whose tests failed is a result a reviewer judges, and a `Produced` round carries no
        // classification at all for the retry policy to act on.
        var produced = RoundResult.Produced("exit 1: 41 tests failed", null);
        Assert.False(produced.IsRetryable);
        Assert.Null(produced.Failure);

        var timedOut = RoundResult.TimedOut();
        Assert.False(timedOut.IsRetryable);
        Assert.Null(timedOut.Failure);
        Assert.Null(timedOut.Log);
    }
}

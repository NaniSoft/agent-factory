namespace AgentFactory.Tests.Boundary;

using AgentFactory.Failures;
using AgentFactory.Rounds;

/// <summary>
/// The agent, faked. One call is one round: it hands back a value the test wrote down
/// and keeps what it was asked for, so a test can see how many rounds ran and what
/// each was told. There is no container behind it and nothing to wait on.
/// </summary>
/// <remarks>
/// The three ways a round can come back are kept apart on purpose, because they are what
/// the retry policy is decided on: a round that produced a result, a round that failed
/// transiently, and a round that failed permanently. A fourth — a round whose call throws
/// an exception that has not classified itself — is <see cref="Throwing"/>, and it exists
/// because "unclassified means not retried" is a policy that has to be testable too.
/// </remarks>
public sealed class FakeNOpenCode : INOpenCode
{
    private readonly Queue<Func<CancellationToken, Task<RoundResult>>> _scripted = new();
    private readonly List<Round> _askedFor = [];
    private readonly Queue<TaskCompletionSource<RoundResult>> _held = new();
    private int _ended;

    /// <summary>Every round the factory asked for, in the order it asked.</summary>
    public IReadOnlyList<Round> AskedFor => _askedFor;

    /// <summary>
    /// True once the factory has ended a round itself rather than waiting for it. The
    /// round's token is the signal: a stuck round here has no task to complete, so the
    /// only thing that can end it is the factory cancelling.
    /// </summary>
    public bool EndedARound => Volatile.Read(ref _ended) > 0;

    /// <summary>The next round comes straight back with this result.</summary>
    public FakeNOpenCode Yielding(RoundResult result)
    {
        _scripted.Enqueue(_ => Task.FromResult(result));
        return this;
    }

    public FakeNOpenCode Yielding(RoundOutcome outcome, string? resultPayload = null, string? agentNote = null) =>
        Yielding(outcome == RoundOutcome.Produced
            ? RoundResult.Produced(resultPayload, agentNote)
            // A scripted outcome that is not a result is a failure, and a test that scripts
            // one without saying which kind gets the safe reading: permanent, one attempt.
            : RoundResult.Failed(FailureClass.Permanent));

    /// <summary>The next round ran, changed files, and came back with a result.</summary>
    public FakeNOpenCode Producing(string? resultPayload = null, string? agentNote = null) =>
        Yielding(RoundResult.Produced(resultPayload, agentNote));

    /// <summary>
    /// The next round never got to run: the container would not start, or the files could
    /// not be lifted out of it. Worth another attempt, and the test decides how many there
    /// are by scripting as many of these as it wants to see.
    /// </summary>
    public FakeNOpenCode FailingTransiently() =>
        Yielding(RoundResult.Failed(FailureClass.Transient));

    /// <summary>
    /// The next round ran and its own work failed. Not worth another attempt at all, which
    /// is the half of the ticket that is easy to get wrong in the direction of retrying.
    /// </summary>
    public FakeNOpenCode FailingPermanently() =>
        Yielding(RoundResult.Failed(FailureClass.Permanent));

    /// <summary>
    /// The next round never comes back on its own. Either <see cref="Release"/> ends it
    /// as a round that eventually finished would, or the round timeout ends it.
    /// </summary>
    public FakeNOpenCode Stuck()
    {
        _scripted.Enqueue(_ =>
        {
            var held = new TaskCompletionSource<RoundResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _held.Enqueue(held);
            return held.Task;
        });

        return this;
    }

    /// <summary>
    /// The next round fails the way a seam that has not learned to classify its failures
    /// does: the call comes back faulted with an ordinary exception, saying nothing about
    /// whether another attempt would help.
    /// </summary>
    public FakeNOpenCode Throwing(string? message = null)
    {
        _scripted.Enqueue(_ => Task.FromException<RoundResult>(
            new InvalidOperationException(message ?? "the round came back without a result")));

        return this;
    }

    /// <summary>Lets the oldest held round return, which is how a slow round looks from here.</summary>
    public void Release(RoundResult? result = null)
    {
        if (_held.Count == 0)
        {
            throw new InvalidOperationException("no round is being held");
        }

        _held.Dequeue().SetResult(result ?? RoundResult.Produced(null, null));
    }

    public Task<RoundResult> RunRoundAsync(Round round, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);

        _askedFor.Add(round);
        cancellationToken.Register(() => Interlocked.Increment(ref _ended));

        if (_scripted.Count == 0)
        {
            throw new InvalidOperationException(
                $"the factory asked for a round on issue {round.IssueNumber} and the fake agent has nothing scripted for it");
        }

        return _scripted.Dequeue()(cancellationToken);
    }
}

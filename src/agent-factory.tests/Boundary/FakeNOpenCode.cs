namespace AgentFactory.Tests.Boundary;

using AgentFactory.Rounds;

/// <summary>
/// The agent, faked. One call is one round: it hands back a value the test wrote down
/// and keeps what it was asked for, so a test can see how many rounds ran and what
/// each was told. There is no container behind it and nothing to wait on.
/// </summary>
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
        Yielding(new RoundResult(outcome, resultPayload, agentNote));

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
    /// The next round fails the way an agent whose container died would: the call comes
    /// back faulted rather than with a result.
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

        _held.Dequeue().SetResult(result ?? new RoundResult(RoundOutcome.Produced, null, null));
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

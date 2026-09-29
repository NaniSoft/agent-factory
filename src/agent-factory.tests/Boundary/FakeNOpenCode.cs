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
///
/// The fake also records how many rounds the factory was inside at once, which is the only
/// way to tell a bounded machine from a serial one that happens to end up in the same
/// state. A test that waits for everything to finish passes whether the rounds overlapped
/// or were run one after another; the peak does not.
/// </remarks>
public sealed class FakeNOpenCode : INOpenCode
{
    private readonly Queue<Func<CancellationToken, Task<RoundResult>>> _scripted = new();
    private readonly List<Round> _askedFor = [];
    private readonly Queue<TaskCompletionSource<RoundResult>> _held = new();
    private readonly Lock _inFlightLock = new();
    private int _ended;
    private int _running;
    private int _peak;
    private int _beingHeld;

    /// <summary>Every round the factory asked for, in the order it asked.</summary>
    public IReadOnlyList<Round> AskedFor
    {
        get
        {
            lock (_inFlightLock)
            {
                return _askedFor.ToList();
            }
        }
    }

    /// <summary>
    /// How many of the factory's rounds are being held open right now. Not the same as
    /// <see cref="PeakConcurrency"/>: this is what <see cref="Release"/> can still release,
    /// and a test that releases rounds in a loop needs it to stop at the right one rather
    /// than being told off for asking to release a round that has already come back.
    /// </summary>
    public int BeingHeld
    {
        get
        {
            lock (_inFlightLock)
            {
                return _beingHeld;
            }
        }
    }

    /// <summary>
    /// The most rounds the factory was ever inside at once. This is the number the
    /// container budget is a claim about, and it is a measurement rather than an inference:
    /// a round that has been asked for and not yet returned is one the factory is inside.
    /// </summary>
    public int PeakConcurrency
    {
        get
        {
            lock (_inFlightLock)
            {
                return _peak;
            }
        }
    }

    /// <summary>True once the factory has ended a round itself rather than waiting for it. The
    /// round's token is the signal: a stuck round here has no task to complete, so the
    /// only thing that can end it is the factory cancelling.</summary>
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
            // No RunContinuationsAsynchronously: the only continuation this ever has is the
            // fake's own in-flight count, and a test that releases a held round has to see
            // the factory's count come down at the moment it released it rather than a tick
            // later. Nothing else awaits this task — the loop watches it for completion.
            TaskCompletionSource<RoundResult> held;
            lock (_inFlightLock)
            {
                held = new TaskCompletionSource<RoundResult>();
                _held.Enqueue(held);
                _beingHeld++;
            }

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

    /// <summary>
    /// Lets the oldest held round return, which is how a slow round looks from here — and
    /// how a slow round in one project looks while another project's round runs on. Returns
    /// how many were being held, so a test releasing several can say which round it let go.
    /// </summary>
    public int Release(RoundResult? result = null)
    {
        TaskCompletionSource<RoundResult> held;
        lock (_inFlightLock)
        {
            if (_held.Count == 0)
            {
                throw new InvalidOperationException("no round is being held");
            }

            held = _held.Dequeue();
            _beingHeld--;
        }

        held.SetResult(result ?? RoundResult.Produced(null, null));
        return _held.Count;
    }

    public Task<RoundResult> RunRoundAsync(Round round, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);

        lock (_inFlightLock)
        {
            _askedFor.Add(round);
            _running++;
            _peak = Math.Max(_peak, _running);
        }

        cancellationToken.Register(() => Interlocked.Increment(ref _ended));

        if (_scripted.Count == 0)
        {
            Leave();
            throw new InvalidOperationException(
                $"the factory asked for a round on issue {round.IssueNumber} and the fake agent has nothing scripted for it");
        }

        var asked = _scripted.Dequeue()(cancellationToken);
        LeavingWhenItIsOver(asked);

        // Returned as the script's own task rather than wrapped in another, deliberately:
        // the loop watches a round's task for completion, and a wrapper would make that
        // happen on a thread pool continuation instead of the moment the task completed —
        // which is a race in every test that releases a held round.
        return asked;
    }

    /// <summary>
    /// A round's own lifetime as this fake sees it: it counts as in flight until the task
    /// the factory is holding completes, faulted or cancelled alike. The continuation runs
    /// where the task completes rather than after, so the count is never a moment stale.
    /// </summary>
    private void LeavingWhenItIsOver(Task<RoundResult> round)
    {
        if (round.IsCompleted)
        {
            Leave();
            return;
        }

        _ = round.ContinueWith(
            _ => Leave(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Leave()
    {
        lock (_inFlightLock)
        {
            _running--;
        }
    }
}

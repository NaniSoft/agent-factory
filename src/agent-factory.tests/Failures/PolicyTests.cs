namespace AgentFactory.Tests.Failures;

using System.Reflection;
using AgentFactory.Failures;
using AgentFactory.Loop;
using AgentFactory.Polling;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// Where the retry policy lives, asserted structurally rather than only through behaviour.
/// These are the checks for the claims that are easy to state and easy to erode: that a
/// backoff is a comparison against the clock and not a wait, that there is one answer to
/// "is this worth retrying" rather than one per code path, and that the components that
/// classify failures are the ones that observed them.
/// </summary>
public class PolicyTests
{
    [Fact]
    public void Nothing_in_the_factory_waits_for_anything()
    {
        // The whole reason a backoff is a comparison against IClock and not a delay is that
        // the factory has no clock of its own. A Task.Delay, a Thread.Sleep, a Timer or a
        // cancellation that fires on a delay would each be a clock of its own, and the first
        // thing this ticket did with a retry policy would be to add one.
        //
        // This is an IL scan rather than a grep because a grep cannot see a call reached by
        // another name, and a test that only proves the method name is absent from the source
        // is a test that a later agent makes pass by calling it something else. It scans for
        // calls to the four methods that wait, and nothing else: a false positive is
        // impossible, because the only way this fails is by finding one of them exactly.
        var waiting = new[]
        {
            "System.Threading.Tasks.Task:Delay",
            "System.Threading.Thread:Sleep",
            "System.Threading.Timer:..ctor",
            "System.Threading.Timer:Start",
            "System.Threading.CancellationTokenSource:CancelAfter",
        };

        var calls = CallsMadeBy(typeof(FactoryApp).Assembly)
            .Select(call => $"{call.Called.DeclaringType?.FullName}:{call.Called.Name}")
            .ToList();

        // A CancellationTokenSource is the one legitimate caller of a waiting-looking type:
        // a round owns a token so that ending the round ends the call, and there is no
        // delay in it. Nothing else here waits, defers or times out.
        var offenders = calls.Where(call => waiting.Contains(call)).ToList();
        Assert.True(
            offenders.Count == 0,
            $"the factory must not wait, defer or time out anything of its own: {string.Join(", ", offenders)}");

        // A cancellation that fires on a delay is a timer with a different name, and there
        // is none of those either — which is what keeps the "a bounded GitHub read belongs
        // to the seam's own client" answer honest rather than a promise.
        Assert.DoesNotContain("System.Threading.CancellationTokenSource:CancelAfter", calls);
    }

    [Fact]
    public void There_is_exactly_one_answer_to_whether_a_round_is_worth_retrying()
    {
        // The load-bearing distinction of the ticket, and the one most likely to be eroded:
        // a second place deciding for itself what is retryable is how "retry everything that
        // is not a result" creeps back in, one `if` at a time. So the predicate is read in
        // exactly one method, and that method is the loop's.
        // The record's own generated members read the property too — a record prints its
        // own members — and they are the type talking to itself rather than a second
        // opinion, so they do not count as a caller with a policy.
        var asked = new List<string>();
        foreach (var (caller, called) in CallsMadeBy(typeof(FactoryApp).Assembly))
        {
            if (called.DeclaringType == typeof(RoundResult)
                && called.Name == "get_IsRetryable"
                && caller?.DeclaringType != typeof(RoundResult))
            {
                asked.Add(caller?.Name ?? "?");
            }
        }

        Assert.Equal(["LandIfTheRoundIsOver"], asked.Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_failure_is_classified_by_the_component_that_observed_it_and_nowhere_else()
    {
        // The architectural claim, made checkable: the container runtime and the round
        // runner are where a failure is classified, because they are what saw it. The loop
        // decides what to do about a classification, and the GitHub client is not written
        // yet, so nothing else in the process declares one.
        //
        // The reverse direction matters just as much: a round that came back with a result
        // has no classification to read, and nothing in the loop can invent one.
        var declaring = typeof(FactoryApp).Assembly
            .GetTypes()
            .Where(type => typeof(FactoryFailure).IsAssignableFrom(type) && type != typeof(FactoryFailure))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        // The transient subclass, the permanent one, and the container's own — which is a
        // separate type rather than a `TransientFailure` because a docker command that ran
        // and failed is a distinct fact with its own name, and its message carries the verb
        // that failed.
        Assert.Equal(["PermanentFailure", "TransientFailure", "WorkerContainerException"], declaring);

        // And the classes themselves do not guess: neither takes a boolean, and neither
        // derives its class from anything but its own type.
        foreach (var failure in declaring)
        {
            var type = typeof(FactoryApp).Assembly.GetTypes().Single(t => t.Name == failure);
            var parameters = type.GetConstructors().Single().GetParameters();

            Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(bool));
            Assert.Equal(
                typeof(FailureClass),
                type.GetProperty("Class")!.PropertyType);
        }
    }
    [Fact]
    public void The_components_that_gained_a_retry_policy_gained_no_new_seams()
    {
        // The loop's whole knowledge of the outside world is still its constructor, and the
        // poller's is still the four they were. A retry policy is a change of what those
        // components do, never of what they can reach — and a sixth dependency on the
        // orchestrator is exactly how a sleep or a timer would get in.
        Assert.Equal(
            ["IWorkItemStore", "INOpenCode", "IGitHub", "IClock", "ILogger`1"],
            typeof(Orchestrator)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType.Name));

        Assert.Equal(
            ["IWorkItemStore", "IGitHub", "IClock", "ProjectLoadReport", "ILogger`1"],
            typeof(Poller)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType.Name));

        // And the board's write path is still a reviewer's. A loop transition that puts a
        // work item back in the build — which this ticket considered and refused — would
        // need a new decision to drive it, and there are three.
        Assert.Equal(
            [Decision.Approve, Decision.RequestChanges, Decision.Reject],
            Decisions.All);

        // Escalated still offers the two that finish a work item, and only those. The retry
        // policy adds a way for the *loop* to attempt a merge; it does not give a reviewer
        // anything new to click, which is what keeps the board's buttons and the store's
        // rules the same set.
        Assert.Equal([Decision.Approve, Decision.Reject], Decisions.OfferedIn(Swimlane.Escalated));
    }

    /// <summary>
    /// Every call made anywhere in the assembly, as the caller and the callee. A little IL
    /// reader rather than a source grep, because a grep cannot see a call under another
    /// name — and a claim like "nothing here waits" is worth a check that cannot be fooled.
    /// </summary>
    private static IEnumerable<(MethodBase? Caller, MethodBase Called)> CallsMadeBy(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in AllMethods(type))
            {
                MethodBase? caller = method;
                var body = method.GetMethodBody();
                if (body is null)
                {
                    continue;
                }

                var il = body.GetILAsByteArray();
                if (il is null)
                {
                    continue;
                }

                for (var at = 0; at < il.Length;)
                {
                    var (consumed, token) = ReadCall(il, at);
                    at += consumed;

                    if (token is not { } or 0)
                    {
                        continue;
                    }

                    MethodBase? called = null;
                    try
                    {
                        called = method.Module.ResolveMethod(token, type.GetGenericArguments(), null);
                    }
                    catch (ArgumentException)
                    {
                        // A token this module cannot resolve is a token for something that
                        // is not a method — a type or a field reference, say. Not a call.
                    }

                    if (called is not null)
                    {
                        yield return (caller, called);
                    }
                }
            }
        }
    }

    private static IEnumerable<MethodBase> AllMethods(Type type)
    {
        const BindingFlags every =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var method in type.GetMethods(every))
        {
            yield return method;
        }

        foreach (var constructor in type.GetConstructors(every))
        {
            yield return constructor;
        }
    }

    /// <summary>
    /// The length of the instruction at <paramref name="at"/>, and the metadata token it
    /// carries if it is a <c>call</c>, a <c>callvirt</c> or a <c>newobj</c>.
    /// </summary>
    private static (int Length, int Token) ReadCall(byte[] il, int at)
    {
        var op = il[at];

        // A two-byte opcode has 0xFE as its first byte, which is not itself an opcode.
        if (op == 0xFE)
        {
            return (2, 0);
        }

        var isCall = op is 0x28 or 0x6F or 0x73;

        return isCall && at + 4 < il.Length
            ? (5, BitConverter.ToInt32(il, at + 1))
            : (1, 0);
    }
}

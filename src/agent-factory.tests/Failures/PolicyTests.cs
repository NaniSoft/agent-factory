namespace AgentFactory.Tests.Failures;

using System.Reflection;
using AgentFactory.Credentials;
using AgentFactory.Failures;
using AgentFactory.Loop;
using AgentFactory.Polling;
using AgentFactory.Results;
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
    public void Only_the_round_runner_can_put_a_credential_value_into_a_worker_container()
    {
        // ADR-0006 as a structural property rather than a matter of care. One component in
        // the process may turn a credential *name* into a credential *value*, and it is the
        // one that hands a round to a container. Every other component that touches a
        // credential holds the name and nothing else, so a GitHub token cannot reach a
        // worker container by a route nobody re-checked.
        //
        // It is checked by who calls the reader rather than by what the runner does with
        // what it reads, because the caller is the boundary: one caller is a line that can
        // be read and argued with, and a second is a second way in.
        var readers = CallsMadeBy(typeof(FactoryApp).Assembly)
            .Where(call => call.Called.DeclaringType?.Name == "ICredentialReader"
                && call.Called.Name == "Read"
                && call.Caller?.DeclaringType != typeof(ICredentialReader))
            .Select(call => $"{call.Caller?.DeclaringType?.Name}.{call.Caller?.Name}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["WorkerRoundRunner.EnvironmentFor"], readers);

        // And the same check the other way round: the GitHub key name is never an argument
        // to anything that could resolve it. The project's record holds the name, the
        // runner holds the reader, and nothing connects them outside EnvironmentFor.
        var uses = CallsMadeBy(typeof(FactoryApp).Assembly)
            .Where(call => call.Called.DeclaringType == typeof(ICredentialReader))
            .Select(call => call.Caller?.DeclaringType?.Name)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["WorkerRoundRunner"], uses);
    }

    [Fact]
    public void The_deriver_has_no_way_to_ask_the_agent_anything()
    {
        // ADR-0011's guarantee, made structural. The component that produces a result reads
        // a file and reads an environment; it holds no agent, no client and no transport, so
        // "the factory derives the result and the agent only narrates" is a property of the
        // constructor rather than a claim about the code inside it. A deriver that gained a
        // way to prompt a model would be able to believe one, and every test above would
        // still pass.
        Assert.Equal(
            ["ILogger`1"],
            typeof(RoundResultDeriver)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType.Name));

        // Nothing sleeps, defers or times out here either, for the same reason as everywhere
        // else: a deriver that waited would be a wait in the middle of a round.
        var calls = CallsMadeBy(typeof(FactoryApp).Assembly)
            .Select(call => $"{call.Called.DeclaringType?.FullName}:{call.Called.Name}")
            .ToList();

        Assert.DoesNotContain("System.Threading.Tasks.Task:Delay", calls);
    }

    [Fact]
    public void The_deriver_reads_the_files_changed_out_of_git_and_out_of_nowhere_else()
    {
        // The one thing a result must not be, asserted structurally: a `ChangedFile` cannot
        // be built anywhere in the process except the two methods that read `git status` and
        // the diff. If a result could name a file that git did not report, the property
        // "files changed come from the container's git state" would be a convention rather
        // than the shape of the code — and the note would be a way in.
        //
        // A record's own generated members are excluded for the reason the retry test
        // excludes them: `with` and the compiler's copy constructor are the type talking to
        // itself, and one of them adds a field git reported to another git reported. They
        // cannot introduce a path that was not already there.
        var constructors = ConstructorsOf<ChangedFile>();

        // Two construction sites, and both are the git half of the deriver: the one that
        // reads a path out of the diff, and the one that unions the two observations
        // together.
        Assert.Equal(["RoundResultDeriver.DiffFiles", "RoundResultDeriver.FilesFrom"], constructors);

        // The same for a command: a `CommandOutcome` can only come from a record the image's
        // wrapper wrote, so the factory cannot invent a command a round did not run.
        Assert.Equal(["RoundResultDeriver.CommandFrom"], ConstructorsOf<CommandOutcome>());
    }

    /// <summary>
    /// Every method in the process that constructs a <typeparamref name="T"/>, the record's
    /// own generated members excluded.
    /// </summary>
    private static IReadOnlyList<string> ConstructorsOf<T>() => CallsMadeBy(typeof(FactoryApp).Assembly)
        .Where(call => call.Called.DeclaringType == typeof(T)
            && call.Called.Name == ".ctor"
            && Owner(call.Caller) != typeof(T))
        .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}")
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// A method's own name, and for an iterator the name of the method it was compiled
    /// from rather than the state machine's `MoveNext` — which is where the work happens,
    /// and which would name the same method twice for no gain. The name is recovered from
    /// the compiler's own artefact, `<DiffFiles>d__17`, because that is the only place it
    /// survives.
    /// </summary>
    private static string Method(MethodBase? method)
    {
        var name = method?.Name ?? "?";
        if (!name.StartsWith("MoveNext", StringComparison.Ordinal))
        {
            return name;
        }

        var artefact = method?.DeclaringType?.Name ?? string.Empty;
        var open = artefact.IndexOf('<');
        var close = artefact.IndexOf('>', open + 1);

        return open >= 0 && close > open ? artefact[(open + 1)..close] : name;
    }

    /// <summary>
    /// The type a method really belongs to. An iterator's body compiles into a nested
    /// `<Name>d__17` class, so `DeclaringType` names the compiler's own artefact rather than
    /// the method's owner — and an assertion on that name would be an assertion on the
    /// compiler's numbering, which is not a fact about this code.
    /// </summary>
    private static Type? Owner(MethodBase? method)
    {
        var type = method?.DeclaringType;

        while (type is { IsNested: true } && type.Name.Contains('<', StringComparison.Ordinal))
        {
            type = type.DeclaringType;
        }

        return type;
    }

    [Fact]
    public void The_board_and_the_loop_gained_no_write_path_and_no_new_decision()
    {
        // The result work added a great deal of text to the board and a log column to the
        // store. Neither is a way for a human to change anything, and the decision set is
        // still three — the same check #7 made, re-run because this ticket touched the
        // board's markup and the store's schema.
        Assert.Equal(
            [Decision.Approve, Decision.RequestChanges, Decision.Reject],
            Decisions.All);

        Assert.Equal([Decision.Approve, Decision.Reject], Decisions.OfferedIn(Swimlane.Escalated));

        // The board's write path is still the one reviewer's form. A second handler would be
        // a second way a human could change a work item, and the loop would be the only
        // component applying policy — so a page that could write a result itself would be a
        // page with an opinion.
        Assert.Equal(["OnGet", "OnPostDecision"], typeof(Pages.IndexModel)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name.StartsWith("On", StringComparison.Ordinal))
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal));
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

namespace AgentFactory.Tests.Failures;

using AgentFactory.Application.Credentials;
using AgentFactory.Application.Observability;
using AgentFactory.Application;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.WorkItems;
using AgentFactory.Infrastructure.Credentials;
using AgentFactory.Loop;
using AgentFactory.Polling;
using AgentFactory.Application.Results;
using AgentFactory.Domain.Results;
using AgentFactory.Infrastructure.Results;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;
using System.Reflection;

/// <summary>
/// Where the retry policy lives, asserted structurally rather than only through behaviour.
/// These are the checks for the claims that are easy to state and easy to erode: that a
/// backoff is a comparison against the clock and not a wait, that there is one answer to
/// "is this worth retrying" rather than one per code path, and that the components that
/// classify failures are the ones that observed them.
/// <para>
/// **Every scan in here reads all four of the factory's assemblies, and that is the
/// whole of what the four-project split changed (#61).** Not one expected list below
/// moved, and not one pin was deleted: all eighteen are claims about a method's callers,
/// a record's construction sites, a type's constructor shape or a vocabulary that is
/// closed, and **none of those is a claim about which project a type sits in.** A project
/// reference can make an outward dependency impossible to compile; it cannot say who may
/// call `Orchestrator.AskForTheRound`, may construct a `ProcessRequest`, may read
/// <c>RoundResult.IsRetryable</c> or may pass <c>ILogger</c> to the deriver. Those are
/// the rules the compiler has no vocabulary for, which is why the pins outlived the
/// assembly rather than being replaced by it.
/// </para>
/// <para>
/// What the split *would* have broken is the opposite half: a scan pointed at one
/// assembly stops seeing a call that moved, its expected list shortens, and the assertion
/// still compares two lists. So the scan set is <see cref="TheFactory.Assemblies"/>,
/// named, and <c>ArchitectureTests</c> pins it — because "the set has four names" and
/// "the walk reads four assemblies" are different claims and only the second one stops the
/// shortening.
/// </para>
/// </summary>
public class PolicyTests
{
    [Fact]
    public void Nothing_in_the_factory_waits_for_anything_except_the_one_thing_that_has_to()
    {
        // The whole reason a backoff is a comparison against IClock and not a delay is that
        // the factory has no clock of its own. A Task.Delay, a Thread.Sleep, a Timer or a
        // cancellation that fires on a delay would each be a clock of its own, and the
        // first thing a retry policy does is to add one.
        //
        // **This assertion changed, deliberately and visibly, twice: for the heartbeat, and
        // again for the workflow worker's reconnect loop.**
        // It used to read "there is no Task.Delay anywhere in the assembly", which was
        // right until the heartbeat landed and wrong the moment it did. The heartbeat is
        // the one legitimate exception: a driver that does not wait cannot know when to
        // tick, and every other cadence in this factory is a TimeSpan compared against
        // IClock. Rather than weaken the check to "Task.Delay is allowed now", it has been
        // *tightened* — to name the single permitted call site. One Task.Delay, in the
        // driver's own scheduling loop, and nothing else anywhere. A second one, in the
        // loop or the deriver or the poller, fails here exactly as it did before.
        //
        // This is an IL scan rather than a grep because a grep cannot see a call reached by
        // another name, and a test that only proves the method name is absent from the source
        // is a test that a later agent makes pass by calling it something else. It scans for
        // calls to the four methods that wait, and nothing else: a false positive is
        // impossible, because the only way this fails is by finding one of them exactly.
        var calls = CallsMadeBy(TheFactory.Assemblies).ToList();

        // The waits in the process, named. A tick between steps of the machine is a tick
        // and not a wait: nothing is held up behind it, and every cadence it drives — the
        // poll interval, the round timeout, the retry backoffs, the feedback threshold — is
        // still a comparison the policy makes for itself. **The list gained its second
        // name, deliberately and visibly, when the durable workflow landed:** the workflow
        // worker reconnects on its own schedule, and a reconnect loop that does not wait
        // cannot reconnect. Both are hosted services that wait so the policy does not have
        // to; neither is inside the policy.
        Assert.Equal(
            ["FactoryDriver.ExecuteAsync", "TemporalTickets.ExecuteAsync"],
            CallersOf(calls, "System.Threading.Tasks.Task:Delay"));
        // And exactly three, counted rather than de-duplicated. The list above names the
        // methods and would be satisfied by a delay smuggled in anywhere else — the driver
        // owns one, and the workflow worker's reconnect loop owns two, one for a server
        // that would not answer and one for a worker that stopped. All three are the
        // hosted services' own, and there is no fourth.
        Assert.Equal(3, TimesCalled(calls, "System.Threading.Tasks.Task:Delay"));

        var waited = CallersOf(calls, "System.Threading.Thread:Sleep")
            .Concat(CallersOf(calls, "System.Threading.Timer:..ctor"))
            .Concat(CallersOf(calls, "System.Threading.Timer:Start"))
            .Concat(CallersOf(calls, "System.Threading.CancellationTokenSource:CancelAfter"))
            .ToList();

        Assert.True(
            waited.Count == 0,
            "the factory must not sleep, start a timer, or fire a cancellation on a delay of its own: "
                + string.Join(", ", waited));
    }

    [Fact]
    public void The_merger_is_the_first_component_with_a_timeout_and_it_bought_none()
    {
        // The GitHub client is the first component in the process that talks to something
        // with a clock of its own, so it is the first thing that could have introduced a
        // second place the factory keeps time. The loop and the poller both hand the seam
        // `CancellationToken.None` and leave the bound to the client, deliberately — so this
        // is the assertion that says the arrangement cost nothing, and it is here rather
        // than in the client's own tests because the claim is about the *assembly*.
        //
        // It is the first test's two assertions, unchanged, plus the observation that they
        // now have more code to be right about. Both still pass with a client in the
        // process, and both would fail if the client had bounded a request with a
        // cancellation that fires on a delay rather than with the transport's own refusal
        // to hang.
        Assert.Equal(
            ["FactoryDriver.ExecuteAsync", "TemporalTickets.ExecuteAsync"],
            CallersOf(CallsMadeBy(TheFactory.Assemblies), "System.Threading.Tasks.Task:Delay"));

        var timed = CallersOf(CallsMadeBy(TheFactory.Assemblies), "System.Threading.CancellationTokenSource:CancelAfter");
        Assert.True(
            timed.Count == 0,
            "a cancellation that fires on a delay is a clock, and the seam's own bound is a client timeout: "
                + string.Join(", ", timed));

        // And the bound is a named one rather than a default nobody chose. A request with no
        // bound is a merge that can hold a reviewer's click open for ever, which is the one
        // thing the loop's own gate cannot fix: it holds the machine, and the machine is
        // held until the client comes back.
        Assert.True(
            AgentFactory.GitHub.GitHubClient.RequestTimeout > TimeSpan.Zero,
            "a GitHub request with no bound is a merge that can hang for ever");
    }

    [Fact]
    public void The_one_gate_in_the_factory_is_the_loops_step_and_it_waits_for_no_one()
    {
        // With a heartbeat there are two things that can ask the machine for a step at once
        // — a reviewer's click and a tick — and without a gate the concurrency count itself
        // would be a race: two callers could both read one round in flight and both start a
        // second, and the count that is supposed to bound concurrency would bound nothing.
        //
        // So there is one gate on the loop's own step, and it is the only SemaphoreSlim in
        // the process that guards a policy. **The list gained its second name, deliberately
        // and visibly, when the durable workflow landed:** the workflow client's connect is
        // guarded so two board clicks cannot build two clients — a one-at-a-time guard
        // around construction, not a gate on policy. Naming both here is what stops either
        // quietly becoming a queue with a deadline: a gate that could time out would be a
        // second policy, and one with a waiting room would be contention nobody has an
        // opinion about.
        Assert.Equal(
            ["Orchestrator.StepAsync", "TemporalTickets.ClientAsync"],
            CallsMadeBy(TheFactory.Assemblies)
                .Where(call => call.Called.DeclaringType?.Name == "SemaphoreSlim"
                    && call.Called.Name is "WaitAsync" or "Wait")
                .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}")
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList());
    }

    [Fact]
    public void The_round_concurrency_is_a_code_constant_and_rounds_are_started_in_one_place()
    {
        // Two, sized for this machine, on which each round is an agent, a toolchain and a
        // build in its own working tree. Written out rather than read from the constant: a
        // test that read the constant would prove only that the policy is whatever the
        // constant says. And it is code, not configuration: the project file schema has four
        // fields and refuses a fifth, so there is nowhere for a project to raise its own
        // ceiling even if one wanted to.
        Assert.Equal(2, FactoryConstants.RoundConcurrency);

        // And it is code, not configuration. Nothing a project file or an options object can
        // set touches the ceiling, so a project cannot buy itself a third round and no
        // deployment can quietly raise the machine's ceiling: the number is in one class and
        // a change to it is a deliberate edit in one obvious place.
        Assert.DoesNotContain(
            typeof(FactoryOptions).GetProperties(),
            property => property.Name.Contains("Budget", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Concurrent", StringComparison.OrdinalIgnoreCase));

        // The loop is the only component in store-only mode that asks for a round, and the
        // workflow's round activities are the only callers in workflow mode — so the whole
        // of the process's round-starting is three methods in two components, all named.
        // This is the structural half of "the concurrency is what bounds
        // it": a fourth caller of the agent seam would be a second way to start a
        // round, and nothing that counts could see it. Checked by caller
        // rather than by grep, for the reason the rest of this file is.
        //
        // The loop's one caller is its own escape hatch rather than the method that decides
        // to start a round: AskForTheRound is the single place a call to the agent can be
        // made, so an agent that throws before it has a task to hand back becomes a round
        // that came back without a result rather than a fault in the middle of a step. The
        // activities' two are the workflow's own: a route round and a build round, each one
        // activity, each bounded by the engine's own retry policy and concurrency.
        Assert.Equal(
            [
                "Orchestrator.AskForTheRound",
                "TicketActivities.BuildRoundAsync",
                "TicketActivities.RouteRoundAsync",
            ],
            CallsMadeBy(TheFactory.Assemblies)
                .Where(call => call.Called.DeclaringType == typeof(INOpenCode)
                    && call.Called.Name == "RunRoundAsync")
                .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}")
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList());
    }

    /// <summary>
    /// Which methods in the assembly call <paramref name="target"/>, by the real method
    /// rather than by the compiler's own artefact for an async state machine.
    ///
    /// <para>
    /// De-duplicated, and that is load-bearing rather than tidy. The claim is "one wait in
    /// one place", and a list of call <em>sites</em> would let a second <c>Task.Delay</c> in
    /// the same method pass while the count of waits in the process quietly doubled — a
    /// mutation that was tried and is caught by this being a distinct list rather than a
    /// raw one.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> CallersOf(
        IEnumerable<(MethodBase? Caller, MethodBase Called)> calls,
        string target) => calls
        .Where(call => $"{call.Called.DeclaringType?.FullName}:{call.Called.Name}" == target)
        .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}")
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// How many times the assembly calls <paramref name="target"/>, counted rather than
    /// de-duplicated, so that two calls of the same method are two and not one.
    /// </summary>
    private static int TimesCalled(
        IEnumerable<(MethodBase? Caller, MethodBase Called)> calls,
        string target) => calls
        .Count(call => $"{call.Called.DeclaringType?.FullName}:{call.Called.Name}" == target);

    [Fact]
    public void A_rounds_own_exit_code_is_read_in_exactly_one_place_and_no_command_exit_code_is()
    {
        // **Added for #22, and the pin that makes the ticket's central claim structural.**
        //
        // The whole of that ticket is that two different exit codes were being conflated:
        // the exit code of the round's *own last command* (which says whether the round
        // finished) and the exit code of a command *inside* the round (which is data about
        // the work). The second real run had a round whose agent was refused by a provider
        // rate limit reported as `Produced` with an empty diff, because the first number was
        // written into every result file and read by nothing.
        //
        // So this pins two things. `roundExitCode` is read in one place and one place only,
        // and that place is the round runner — the component that observed the round, and
        // the one the design's rule says classifications are made by. And nothing in the
        // process reads `FailedCommands`, or `CommandOutcome.ExitCode`, to decide an
        // outcome: a build whose tests failed is `Produced` and carries no classification at
        // all, and a policy that consulted the command exit codes would make every one of
        // them retryable, which is what DESIGN.md says not to do.
        var assemblies = TheFactory.Assemblies;

        // Where the number comes from and where it is used. It is read out of the result
        // file's header by the deriver, put in front of a reviewer by the payload, and
        // decided on in exactly one place — the round runner, the component that observed
        // the round. Anything else that wanted to know whether a round finished has to come
        // through `TheRoundRan`, which is the only shape the decision is made in.
        //
        // The record's own generated members are excluded for the reason the retry test
        // excludes them: `with`, `Deconstruct` and the compiler's copy constructor are the
        // type talking to itself rather than a second opinion, and a test that counted them
        // would be asserting on the compiler rather than on the policy.
        var read = CallsMadeBy(assemblies)
            .Where(call => call.Called.Name == "get_RoundExitCode" || call.Called.Name == "get_TheRoundRan")
            .Where(call => Owner(call.Caller) != typeof(RoundEnvironment))
            // The same exclusion, for the collector's own header record: its generated
            // print and deconstruct read every field it holds, which is the type talking
            // to itself rather than a second opinion about whether a round finished.
            .Where(call => Method(call.Caller) is not ("Deconstruct" or "PrintMembers"))
            .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                // Puts it in front of a reviewer, in the payload, because a failed round's
                // payload is the only account of what stopped it.
                "ResultPayload.Headline",

                // The one decision. Anything else that wanted to know whether a round
                // finished would have to come through this.
                "RoundRunner.RunRoundAsync",
            ],
            read);

        // And the *field* is read by the deriver's header reader alone — `EnvironmentFrom`
        // is the only method in the process that turns a header field into a number, and a
        // second reader would be a second reading of the round's own account of itself,
        // free to disagree with the first.
        var fieldRead = CallsMadeBy(assemblies)
            .Where(call => call.Called.DeclaringType?.Name == "RoundResultDeriver"
                && call.Called.Name == "Int"
                && call.Caller is { } caller
                && Owner(caller)?.Name == "RoundResultDeriver")
            .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        // Two, and both are the readers of a record's fields: the header and the command
        // record. Neither is asked about anything beyond its own shape.
        Assert.Equal(
            ["RoundResultDeriver.CommandFrom", "RoundResultDeriver.EnvironmentFrom"],
            fieldRead);

        // And the command exit codes decide nothing. The readers of `Passed` are a
        // projection over the record and the payload renderer that shows it to a reviewer;
        // **neither the loop nor the round runner reads it**, and that is the claim worth
        // making here rather than a list of method names — a list would have to name the
        // compiler's own artefact for a lambda, which is an assertion on the compiler rather
        // than on the policy.
        //
        // So this is the negative form, and it is the one that catches the mutation: a
        // boundary written as "did anything come back non-zero" would make every failing
        // test retryable, and this fails the moment the round runner or the loop so much as
        // looks at a command's exit code.
        var policyDecidesOn = CallsMadeBy(assemblies)
            .Where(call => call.Called.Name is "get_Passed" or "get_FailedCommands"
                // `ExitCode` is read off a *command record* and off nothing else: the
                // process invocation's own exit code is the round's, and the runner is
                // exactly who is meant to read that one.
                || (call.Called.Name == "get_ExitCode" && call.Called.DeclaringType?.Name != "ProcessInvocation"))
            .Where(call => call.Caller is { } caller
                && Owner(caller) is { } owner
                && owner.Name is "Orchestrator" or "RoundRunner")
            .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            policyDecidesOn.Count == 0,
            "a round's outcome must be decided by the round's own exit code and by nothing a command inside the "
                + "round returned, or a build that fails its tests becomes a transient failure and is retried "
                + "for ever: " + string.Join(", ", policyDecidesOn));

        // The one place a command's exit code is *read* at all, which is the payload showing
        // a reviewer what ran.
        Assert.Contains(
            "ResultPayload.Commands",
            CallsMadeBy(assemblies)
                .Where(call => call.Called.Name == "get_Passed")
                .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}"));

        // The two numbers are two *different fields*, which is the whole of the boundary. A
        // single "did anything fail" flag would put a rate-limited round and a test-failed
        // round in the same place, and this is the assertion that they are not: the round's
        // own exit code is named `roundExitCode` and the command's is `exitCode`, and the
        // deriver reads them into two different types.
        Assert.NotEqual(
            typeof(RoundEnvironment).GetProperty("RoundExitCode"),
            typeof(CommandOutcome).GetProperty("ExitCode"));

        // Which is the structural claim #7 made, restated in the terms this ticket changed.
        // A produced round carries no classification and cannot be retried, whatever its
        // commands returned; a rate-limited round is `Failed` and `Transient` and is. The
        // two are told apart by which number was non-zero, not by a flag both could set.
        Assert.True(RoundResult.Failed(FailureClass.Transient).IsRetryable);
        Assert.False(RoundResult.Failed(FailureClass.Permanent).IsRetryable);
        Assert.False(RoundResult.Produced("exit 1: 41 tests failed", null).IsRetryable);
        Assert.Null(RoundResult.Produced("exit 1: 41 tests failed", null).Failure);
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
        foreach (var (caller, called) in CallsMadeBy(TheFactory.Assemblies))
        {
            if (called.DeclaringType == typeof(RoundResult)
                && called.Name == "get_IsRetryable"
                && caller?.DeclaringType != typeof(RoundResult))
            {
                asked.Add($"{Owner(caller)?.Name}.{Method(caller)}");
            }
        }

        // **Named through `Owner().Method()` rather than through `caller.Name`, which changed
        // when the routing layer made the landing method async.** A method's body compiles
        // into a state machine, so a read from inside an `async` method arrives attributed to
        // `<LandTheRoute>d__37.MoveNext`; reading `caller.Name` alone reported `MoveNext`,
        // which is the compiler's artefact rather than the reader, and a list naming a
        // compiler's artefact names nothing. The recovery is the same one the GitHub-caller
        // pin below uses and for the same reason.
        Assert.Equal(["Orchestrator.LandIfTheRoundIsOverAsync"], asked.Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_failure_is_classified_by_the_component_that_observed_it_and_nowhere_else()
    {
        // The architectural claim, made checkable: the process runner and the round
        // runner are where a failure is classified, because they are what saw it. The loop
        // decides what to do about a classification, and the GitHub client is not written
        // yet, so nothing else in the process declares one.
        //
        // The reverse direction matters just as much: a round that came back with a result
        // has no classification to read, and nothing in the loop can invent one.
        var declaring = TheFactory.Assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(FactoryFailure).IsAssignableFrom(type) && type != typeof(FactoryFailure))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        // The transient subclass and the permanent one — two classes rather than one,
        // because what they guard is the direction of the answer: a failure is declared
        // where it was observed, by being the kind of failure it means, and an unclassified
        // one defaults to permanent rather than to a retry.
        Assert.Equal(["PermanentFailure", "TransientFailure"], declaring);

        // And the classes themselves do not guess: neither takes a boolean, and neither
        // derives its class from anything but its own type.
        foreach (var failure in declaring)
        {
            var type = TheFactory.Assemblies.SelectMany(assembly => assembly.GetTypes()).Single(t => t.Name == failure);
            var parameters = type.GetConstructors().Single().GetParameters();

            Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(bool));
            Assert.Equal(
                typeof(FailureClass),
                type.GetProperty("Class")!.PropertyType);
        }
    }
    [Fact]
    public void Only_the_round_runner_puts_a_credential_into_a_round_and_only_the_merger_resolves_one()
    {
        // ADR-0006 as a structural property rather than a matter of care, and **this test
        // changed deliberately and visibly for the merger**, because the fact it asserted
        // was "one component in the process may turn a credential name into a value" and
        // that stopped being true the moment a merger existed. It was not weakened to
        // accommodate the client: the boundary it guards is still exactly two places, and
        // both are named.
        //
        // The shape of the claim has changed because the *design* has. ADR-0006 says the
        // host holds every write credential — "the host retrieves the commit, pushes the
        // branch, and opens the pull request under the factory's own token" — so a factory
        // that ships a change and never resolves its own GitHub token would be a factory
        // that cannot ship anything. Two readers is what the decision asks for, not a
        // slip in it: the round, which puts a value into an agent's environment, and the
        // merger, which puts a value into a header.
        //
        // What has not changed is the direction, and that is what the two halves assert.
        // The *round* boundary is still one method, and no third component can widen
        // it: a component that could read any credential could hand the GitHub one to the
        // agent, and ADR-0006's whole claim is that it cannot. The merger's read is on the
        // other side of the boundary entirely — it is the host, it is after the round is
        // over, and there is no agent process for it to hand anything to.
        var readers = CallsMadeBy(TheFactory.Assemblies)
            .Where(call => call.Called.DeclaringType?.Name == "ICredentialReader"
                && call.Called.Name == "Read"
                && call.Caller?.DeclaringType != typeof(ICredentialReader)
                // The composite reader's own call to the environment reader is the
                // boundary delegating to itself, not a third consumer: what this scan
                // guards is who turns a name into a value, and the composite is where
                // that happens (#32).
                && call.Caller?.DeclaringType != typeof(CompositeCredentialReader))
            .Select(call => $"{call.Caller?.DeclaringType?.Name}.{call.Caller?.Name}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        // Two, and both are the ones the design names: the round that is starting, and the
        // host that ships what it produced. The composite (#32) is the boundary itself, not
        // a third one — filtered above, named here.
        Assert.Equal(["GitHubClient.TokenFor", "RoundRunner.EnvironmentFor"], readers);

        // The round half, and it is the half that has not moved. A round's environment is
        // built in exactly one place in the process, and that place is inside the round
        // runner: so a credential value can only reach an agent by going through the
        // component whose one credential read is the LLM key's, and ADR-0006's claim
        // holds by shape rather than by care. The merger is not on that path and cannot get
        // onto it, because it is not the thing that starts processes.
        //
        // Checked as *the one construction site* rather than as a name in a list of readers,
        // because the readers list says who asked and this says who can put. A third reader
        // somewhere harmless is caught by the list above; a second way to build a process's
        // environment is caught here, and it is the one that would undo ADR-0006. The
        // request is built twice in the runner — once for the agent, once for the small git
        // questions it asks of the tree — and both are the runner, which is the point.
        Assert.Equal(
            ["RoundRunner.GitAsync", "RoundRunner.RunRoundAsync", "RoundRunner.WorkspaceFor"],
            ConstructorsOf<ProcessRequest>());
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
        // else: a deriver that waited would be a wait in the middle of a round. Checked as
        // *this component* making no waiting call rather than as the assembly containing
        // none, because the heartbeat is the one place in the process where a delay is
        // legitimate and this test must not become the thing that quietly forbids it again.
        Assert.DoesNotContain(
            "RoundResultDeriver",
            CallersOf(CallsMadeBy(TheFactory.Assemblies), "System.Threading.Tasks.Task:Delay"));
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
    /// Every method in the factory that constructs a <typeparamref name="T"/>, the record's
    /// own generated members excluded. Reads all four assemblies like every other scan
    /// here, so a <c>ChangedFile</c> that moved from the deriver to somewhere else would
    /// leave one construction site behind rather than none.
    /// </summary>
    private static IReadOnlyList<string> ConstructorsOf<T>() => CallsMadeBy(TheFactory.Assemblies)
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
    public void The_review_surface_cannot_ask_gitHub_for_a_rounds_change()
    {
        // "The diff renders without depending on GitHub being reachable" is not only an
        // availability property. A diff fetched from the same service that produced the
        // change is a second opinion from the thing under review rather than evidence
        // about it, and it is unavailable exactly when a reviewer most needs to know what
        // an unattended round did.
        //
        // So it is structural rather than behavioural: the component that produces a
        // round's diff holds nothing that could reach GitHub, and nothing in the process
        // calls the GitHub seam on a code path a diff could be on. The seam is called from
        // two places — intake, and the loop merging — and the board is neither.
        //
        // **A third was added for the routing layer and the list below says so
        // (ADR-0015).** `IssuePublisher` files the tickets a routing round proposed, which is
        // a GitHub write on the loop's behalf rather than on a diff's. It is named
        // individually rather than folded into "the loop" so that this stays a short list of
        // named things allowed to reach the network instead of a general permission: the
        // publisher reaches the same single seam, under the same credential, on the same host,
        // after the round that proposed the tickets is gone (ADR-0006), and it is
        // stateless with no transport of its own.
        //
        // **And a fourth family for the durable workflow, deliberately and visibly.**
        // `TicketActivities` reads the conversation a round travels with and merges what a
        // reviewer approved — the same two reads and one write the loop makes, made on the
        // workflow's behalf. The activities hold no transport of their own: they are handed
        // the same seam, and what they may not do is what this test has always said —
        // nothing on a diff's code path reaches GitHub.
        //
        // The availability claim this test exists for is untouched. A reviewer reads a diff
        // long after a routing round filed whatever its verdict asked for, and the diff is
        // still git, on this host, against a directory on this machine.
        // Named by owner and method rather than by declaring type: an async method's body
        // compiles into a state machine, so the caller is `<Approve>d__18` and the method
        // is `MoveNext` unless it is recovered the way `Method` recovers it.
        var readers = CallsMadeBy(TheFactory.Assemblies)
            .Where(call => call.Called.DeclaringType?.Name == "IGitHub")
            .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.All(readers, caller =>
            Assert.True(
                caller.StartsWith("Poller.", StringComparison.Ordinal)
                    || caller.StartsWith("Orchestrator.", StringComparison.Ordinal)
                    || caller.StartsWith("IssuePublisher.", StringComparison.Ordinal)
                    || caller.StartsWith("TicketActivities.", StringComparison.Ordinal),
                $"only intake, the loop, the issue publisher and the workflow activities may ask GitHub anything, and {caller} does: a round's diff is "
                    + "generated on the host from the tree the round left, and a board that fetched one from the API would be showing a "
                    + "second opinion from the service that produced the change (ADR-0006)"));

        // And the diff reader itself is a git process against a directory, with no client,
        // no transport and no credential reader — checked as its own constructor, for the
        // reason the deriver's is: a component that could reach the network could decide
        // to.
        Assert.Empty(typeof(HostDiffReader).GetConstructors());

        foreach (var type in typeof(HostDiffReader).Assembly.GetTypes())
        {
            if (type != typeof(HostDiffReader) && type != typeof(HostDiff))
            {
                continue;
            }

            foreach (var constructor in type.GetConstructors())
            {
                Assert.DoesNotContain(
                    constructor.GetParameters(),
                    parameter => parameter.ParameterType.Name is "IGitHub" or "INOpenCode" or "ICredentialReader"
                        or "HttpClient" or "HttpMessageHandler");
            }
        }
    }

    [Fact]
    public void A_rounds_diff_is_generated_from_git_and_from_nothing_else()
    {
        // The claim the review surface rests on: the diff a reviewer judges is `git diff`
        // against the tree the round left. So the component that produces it starts one
        // process, the git binary, with the arguments a test can read — and it does not
        // also read a result file, ask the GitHub seam, or take a credential. A reader
        // that gained any of those would be a second source of truth about a change, and
        // two sources of truth is how a reviewer ends up judging one change against
        // another's account of it.
        //
        // The process arguments are checked by name because the three flags that make
        // this safe are all in them, and each was a refusal rather than a convenience:
        // `--no-ext-diff` and `--no-textconv` stop git running a program the tree's own
        // configuration names, and `safe.directory` is what makes a tree created by uid
        // 1000 inside a container readable at all on a Linux host.
        Assert.Contains(
            "HostDiffReader.GitAsync",
            CallersOf(CallsMadeBy(TheFactory.Assemblies), "System.Diagnostics.Process:Start"));

        // The refusals, checked as the argument list rather than as source: they are the
        // point of the component, so they are a value it exposes and a test can read.
        // Without `--no-ext-diff` and `--no-textconv` git will run a program named by the
        // tree's own configuration to produce the diff, and the tree is a repository this
        // factory did not write.
        var arguments = HostDiffReader.Arguments("abc1234");
        Assert.Contains("--no-ext-diff", arguments);
        Assert.Contains("--no-textconv", arguments);
        Assert.Contains("--no-color", arguments);
        Assert.Equal("abc1234", arguments[^1]);

        // And line endings, pinned for the same reason #26 exists. Left to the host, the
        // bytes on a card depend on the machine rendering it: this one had
        // core.autocrlf=true, git warned about CRLF for every file, and AGENTS.md's claim
        // that a reviewer can hold the card against their own `git diff` and get the same
        // bytes was only usually true. The round committed inside a Linux container where
        // autocrlf is off, so off is the setting that compares the agent's bytes with
        // themselves.
        var pinned = arguments
            .Select((argument, at) => (argument, at))
            .Where(pair => pair.argument == "-c")
            .Select(pair => pair.at + 1)
            .Select(at => arguments[at])
            .ToList();

        Assert.Contains("core.autocrlf=false", pinned);
        Assert.Contains("core.quotepath=false", pinned);

        // And the one configuration a diff cannot be read without: a tree created by uid
        // 1000 inside a container is a repository git refuses to read on a Linux host, so
        // without this the round's diff would be silently *absent* rather than wrong —
        // which reads as a round that changed nothing.
        Assert.Equal("1", HostDiffReader.GitEnvironment["GIT_CONFIG_COUNT"]);
        Assert.Equal("safe.directory", HostDiffReader.GitEnvironment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("*", HostDiffReader.GitEnvironment["GIT_CONFIG_VALUE_0"]);

        // Nothing in the component's own dependencies reaches a network or a credential.
        Assert.Empty(typeof(HostDiffReader).GetConstructors());
        Assert.DoesNotContain(
            typeof(HostDiff).GetProperties(),
            property => property.PropertyType.Name is "HttpClient" or "IGitHub" or "ICredentialReader");
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

        // The write path is the JSON surface now (#51): the reviewer's decision endpoint
        // and the workspace's open are the two writes, and neither grows a decision. The
        // exact route list and the fact that a decision is the human's one way to move a
        // work item are pinned where the process is actually running, in
        // `DecisionTests.The_board_is_the_only_way_a_human_can_change_anything`; this is
        // the IL half of the same claim, and it is deliberately short because the set of
        // decisions is what belongs in a scan of the application, not a list of handlers.
    }

    [Fact]
    public void A_metric_tag_can_be_built_in_exactly_one_place_and_its_vocabulary_is_closed()
    {
        // The hazard a metric label carries is that a tag's *values* are the keys of a time
        // series somebody will keep for ever. A tag on a work item, a branch, a file path or
        // an issue title is therefore two failures at once: a series per instance that no
        // dashboard can read, and a copy of a stranger's words in a telemetry store whose
        // retention this repository does not control. The design asks for counters and says
        // nothing about how they are labelled, so the labelling is this ticket's judgement
        // and it has to be a property of the code rather than a convention.
        //
        // So it is closed in the strongest sense available: `FactoryMetrics.Tags` is the
        // whole vocabulary, and *this* class is the only place a tag can be constructed at
        // all. Checked by IL rather than by grep for the reason the rest of this file is —
        // a call reached under another name is invisible to a grep, and a later edit that
        // builds a `KeyValuePair<string, object?>` and hands it to a counter would
        // otherwise pass every test in this file.
        //
        // Note what this does *not* claim: that the values passed in are right. A
        // `FactoryMetrics` that labelled a round with its work item id would satisfy this
        // and break the cardinality argument, because it would be a legal call to `Key`.
        // The other half is `ObservabilityTests`, which drives the real components and
        // asserts the *values* every published tag actually carries.
        var built = CallsMadeBy(TheFactory.Assemblies)
            .Where(call => call.Called.DeclaringType?.FullName == "System.Collections.Generic.KeyValuePair`2"
                && call.Called.Name == ".ctor"
                && Owner(call.Caller)?.Name != "FactoryMetrics")
            .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            built.Count == 0,
            "a metric tag may only be built in FactoryMetrics, because the vocabulary is what keeps the cardinality "
                + "and the privacy of this process's metrics a fact rather than a hope: " + string.Join(", ", built));

        // And the vocabulary itself, written out rather than read from the set, so that
        // widening it is a failing test rather than a silent addition of a dimension.
        Assert.Equal(["outcome", "project"], FactoryMetrics.Tags.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void A_record_about_a_work_item_is_written_inside_a_scope_that_names_it()
    {
        // "Every log record carries the work item it belongs to" is not a property of any
        // one message; it is a property of where the message is written. The components that
        // write records about a work item are exactly the ones that are handed one, and this
        // is the check that each of them opens a `WorkItemScope` rather than remembering to
        // name the work item in a placeholder.
        //
        // The list is a closed one on purpose, and it is short because of the argument rather
        // than for tidiness: the loop, the poller, the round runner and the merger are the
        // components in the process that are ever handed a work item's identity. A fifth
        // would be a new component taking one, and the point of the check is that it would
        // have to be argued for here rather than added quietly. The board page used to be a
        // fifth and is gone with the Razor board (#51): the JSON decision endpoint records
        // and steps without opening a scope of its own.
        //
        // **It grew by two entries for the routing layer (ADR-0015)**, and neither is a new
        // component taking a work item's identity:
        //
        // - `Orchestrator.LandTheRoute` is the loop landing a routing round, which is the
        //   same "this round belongs to this work item" fact the other `Orchestrator`
        //   entries already carry. It is a separate method rather than more of
        //   `LandIfTheRoundIsOver` because the two landings write different things: one
        //   records a diff and one records a verdict, and a scope opened once for both would
        //   mean the verdict's records inherit a round number that means something else.
        // - `IssuePublisher.PublishAsync` *is* a new component handed a work item, and it is
        //   here rather than exempted because a component that files issues in somebody's
        //   tracker on a work item's behalf and does not name that work item in its records
        //   is exactly the thing this list exists to make somebody argue for.
        //
        // What this does *not* prove is that every record those components write is inside a
        // scope - a component can open one and then log outside it, and this cannot see that.
        // `TraceabilityTests` is the other half: it drives the real components and reads the
        // identity off the records themselves.
        var opened = CallsMadeBy(TheFactory.Assemblies)
            .Where(call => call.Called.DeclaringType?.Name == "WorkItemScope"
                && call.Called.Name == "ForWorkItem")
            .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "GitHubClient.MergeAsync",
                "IssuePublisher.PublishAsync",
                "Orchestrator.Apply",
                "Orchestrator.HandBackAnAnsweredGrill",
                "Orchestrator.LandIfTheRoundIsOverAsync",
                "Orchestrator.LandTheRoute",
                "Orchestrator.MergeWhatNobodyReviewed",
                "Orchestrator.RecoverOrphanedRounds",
                "Orchestrator.RetryAParkedMerge",
                "Orchestrator.StartARoundAsync",
                "Orchestrator.TryTheRetry",
                "Poller.IntakeFrom",
                "RoundRunner.RunRoundAsync",
            ],
            opened);

        // And the keys a scope can carry, written out rather than read from the constant, so
        // that a fifth kind of ambient identity is a failing test rather than something a
        // record silently grows. Four is the whole list because the work item is a thing with
        // a project and an issue in it and a round number on it, and nothing else about it is
        // something a record needs to be findable by.
        Assert.Equal(
            ["WorkItemId", "WorkItemProject", "WorkItemIssue", "WorkItemRound"],
            WorkItemScope.Keys);
    }

    [Fact]
    public void A_record_about_a_project_is_written_inside_a_scope_that_names_it()
    {
        // The companion to the work item's scope, and it exists because of a hole the work
        // item's scope cannot fill: **a project whose intake failed has no work item.** It
        // has no id, no issue number and no round, so the only identity such a record has
        // is the project it is about, and a fault that is not attributable to a project is
        // a fault an operator has to find by reading lines of prose.
        //
        // It is a second vocabulary rather than a fifth key in the first one, and that is
        // the decision worth stating. `WorkItemScope`'s four keys are work-item-shaped —
        // the work item, its project, its issue, its round — and a project-level record
        // belongs to none of those except the project. Putting the project's name on such a
        // record under `WorkItemProject` would mean a field called "the work item's
        // project" carrying a project with no work item; filling the other three would mean
        // inventing a work item id, an issue number and a round number for a work item that
        // does not exist. So the work item's vocabulary is **unchanged** — the four keys are
        // exactly what they were, and its call sites are the ones pinned above minus the
        // board page's, which is gone with the Razor board (#51) — and this is its own
        // class with its own key and its own pinned list.
        //
        // Nothing was weakened to accommodate it. The claim above is still asserted in full
        // and a new claim is asserted beside it, so widening either vocabulary is still a
        // failing test rather than a silent addition.
        var opened = CallsMadeBy(TheFactory.Assemblies)
            .Where(call => call.Called.DeclaringType?.Name == "ProjectScope"
                && call.Called.Name == "ForProject")
            .Select(call => $"{Owner(call.Caller)?.Name}.{Method(call.Caller)}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        // Intake's two records about a project: the one that says a turn was skipped, and
        // the one that says the turn failed. Both are records about a project and neither
        // is about a work item, because neither has one.
        Assert.Equal(["Poller.StepAsync", "Poller.TakeATurnAt"], opened);

        // And the key, written out rather than read from the constant, so that a second kind
        // of ambient identity is a failing test rather than something a record grows. It is
        // prefixed for the reason the work item's are: a record's own message usually names
        // the project too, and a sink that merged the two would emit the same field twice.
        Assert.Equal(["FactoryProject"], ProjectScope.Keys);
    }

    [Fact]
    public void The_observability_component_can_reach_nothing_the_factory_does_not_already_reach()
    {
        // A new component in this process is a new thing that could be depended on, and the
        // two claims this file makes about the shape of the factory — that the loop's world
        // is its constructor, and that the process holds exactly one transport — are both
        // about what components can reach. So the new one is checked against the same rule
        // rather than trusted: it holds no clock, no transport, no credential reader, no
        // store, no seam and no factory option, and it is the only component in the process
        // that is allowed to be constructed without the host supplying anything.
        //
        // The clock is the one that matters. A metrics component with a clock could start
        // emitting on a schedule, and a schedule is a wait, and there is exactly one wait in
        // this process and `PolicyTests` names it. Nothing here observes time.
        var held = TheFactory.Assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Name == "FactoryMetrics")
            .SelectMany(type => type.GetConstructors())
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["IMeterFactory", "Meter"], held);

        // And the reason it is safe for this component to be a dependency of the loop and
        // the poller at all: `System.Diagnostics.Metrics` is a push API. A consumer attaches
        // to the meter in whatever process it shares, and this assembly publishes no
        // measurement over a socket of any kind — so the counters cannot have quietly become
        // a second inbound surface on a process that binds its board to loopback on purpose
        // (story 64, ADR-0012). An empty list is the whole assertion.
        Assert.Empty(CallersOf(CallsMadeBy(TheFactory.Assemblies), "System.Net.HttpListener:..ctor"));
        Assert.Empty(CallersOf(CallsMadeBy(TheFactory.Assemblies), "System.Net.Sockets.TcpListener:..ctor"));
    }

    [Fact]
    public void The_components_that_gained_a_retry_policy_gained_no_new_seams()
    {
        // The loop's whole knowledge of the outside world is still its constructor, and the
        // poller's is still the four they were. A retry policy is a change of what those
        // components do, never of what they can reach — and a sixth dependency on the
        // orchestrator is exactly how a sleep or a timer would get in.
        //
        // **This list changed, deliberately and visibly, for observability.** It used to end
        // at the logger, five dependencies wide, and the sixth is now `FactoryMetrics`. The
        // claim this test defends did not change, and it is worth being precise about why,
        // because a list of parameter names is not a claim on its own: a process runner
        // could arrive as a package reference, an assembly reference or a shell-out, none of
        // which appears in this list either, and the test says so in its own comment.
        //
        // A metrics sink is not any of those. It holds no clock, no transport, no credential
        // reader, no store, no seam and no options — `The_observability_component_can_reach_
        // nothing_the_factory_does_not_already_reach` asserts that list by hand — and it
        // publishes over an in-process push API rather than opening anything. So the boundary
        // this test is about, the one the loop may not grow, has not moved: there is still no
        // way for the loop to start a container, read a credential or keep time from anything
        // but the four things it was given. What it can now do is *count*, which is a
        // narrower thing than any of the five it already could.
        //
        // **And this list grew by one more, deliberately and visibly, for the routing layer
        // (ADR-0015): `IssuePublisher`.** The same reasoning applies and it is worth spelling
        // out, because "the loop's dependencies did not change" is exactly the claim that
        // erodes one dependency at a time.
        //
        // The publisher is not a boundary. It holds no clock, no transport, no credential
        // reader and no options; it reaches the network only by calling the seam the loop is
        // *already* given, so the loop has not gained a new way to reach anything — it has
        // gained a collaborator that uses an existing one. What it can now do is ask the seam
        // to create an issue, which is narrower than the four things it could already do,
        // because it is a write the loop was already authorised to make (it merges).
        //
        // What is deliberately *not* here is anything the loop would need in order to decide
        // for itself: no credential reader, no HTTP client, no second
        // seam. Those would be a second opinion about a repository, and this list is the
        // check that they cannot arrive.
        Assert.Equal(
            [
                "IWorkItemStore",
                "INOpenCode",
                "IGitHub",
                "IssuePublisher",
                "IClock",
                "ILogger`1",
                "FactoryMetrics",
                "FactoryOptions",
            ],
            typeof(Orchestrator)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType.Name));

        // The poller gained two dependencies along the way, each for one reason and no
        // other: the metrics, because the count of issues turned into work items is a fact
        // only intake has; and the ticket starter, because a work item intake makes is the
        // ticket whose durable workflow begins — the starter is the no-op that is not there
        // in store-only mode, and it holds no clock, no transport and no policy of its own.
        Assert.Equal(
            ["IWorkItemStore", "IGitHub", "ITicketStarter", "IClock", "ProjectLoadReport", "ILogger`1", "FactoryMetrics"],
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
    /// Every call made anywhere in the assemblies it is given, as the caller and the
    /// callee. A little IL reader rather than a source grep, because a grep cannot see a
    /// call under another name — and a claim like "nothing here waits" is worth a check
    /// that cannot be fooled.
    /// <para>
    /// **It takes a set rather than one assembly, and that changed with #61.** It took a
    /// single assembly because there was a single assembly, and every expected list in
    /// this file was written against all of it. The four-project split does not change any
    /// of those claims — they are all claims about *callers*, *construction sites* or
    /// *constructor shapes*, and none of them is a claim about which project a type sits
    /// in — so none of the lists below needed editing. What a split would have changed,
    /// if this had stayed pointed at one assembly, is what the scan could see: a call that
    /// moved to `Infrastructure` would leave the scanned assembly, the list it appears in
    /// would get one entry shorter, and `Assert.Equal` would still be comparing two lists.
    /// <c>Assert.Equal</c> against a list the scan shortened is the silent failure, and
    /// the only defence is the scan set.
    /// </para>
    /// <para>
    /// So the set is <see cref="TheFactory.Assemblies"/> — all four, by name — and it is
    /// not widened to "whatever is beside the test binaries", because a discovery that
    /// quietly absorbed a fifth assembly would be a scan nobody had read. A fifth project
    /// is a deliberate decision and <c>ArchitectureTests</c> says what it has to do to
    /// this file.
    /// </para>
    /// <para>
    /// It stays hand-rolled. The two things it does that a general-purpose
    /// architecture-test library gets wrong most often are both here: <see cref="Owner"/>
    /// and <see cref="Method"/> recover the real method behind the compiler's
    /// <c>&lt;Name&gt;d__17</c> state-machine artefact, and <see cref="Instructions"/>
    /// takes instruction lengths from the runtime's own opcode table after a byte-at-a-time
    /// scan silently stepped over a real call. A replacement would have to re-derive both
    /// against a framework whose answers are read out of the same metadata, and the point
    /// of this file is that its answers are not guesses.
    /// </para>
    /// </summary>
    private static IEnumerable<(MethodBase? Caller, MethodBase Called)> CallsMadeBy(params Assembly[] assemblies)
    {
        foreach (var type in TypesIn(assemblies))
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

                foreach (var at in Instructions(il))
                {
                    var token = BitConverter.ToInt32(il, at + 1);

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

    /// <summary>
    /// Every type in every one of the assemblies it is given — the walk the call scan is
    /// built on, factored out so that it can be asserted on.
    /// <para>
    /// It exists for the one thing a widened scan cannot be trusted on its own to show:
    /// that it reads all of them. Every line of the factory is still in
    /// <c>AgentFactory.Web</c>, so a scan that took the set and read only its first entry
    /// would satisfy every pin in this file today, correctly and silently.
    /// <c>ArchitectureTests</c> asserts the count here against the assemblies' own
    /// <c>GetTypes()</c>, which is what turns "the set has four names" into "the walk
    /// visits four assemblies".
    /// </para>
    /// </summary>
    internal static IEnumerable<Type> TypesIn(params Assembly[] assemblies) =>
        assemblies.SelectMany(assembly => assembly.GetTypes());

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
    /// The offset of every <c>call</c>, <c>callvirt</c> and <c>newobj</c> in a method body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This used to be a scan that advanced one byte at a time and only read a
    /// token when it happened to be standing on one of the three call opcodes.</strong> That
    /// works only while the reader's position happens to land on an opcode boundary, and
    /// that is luck rather than a property: <c>ldarg.s</c> is two bytes, <c>ldc.i4</c> is
    /// five, and a scan that steps over them lands inside operands. It then reads a byte of
    /// somebody's constant as if it were an opcode, skips a length that has nothing to do
    /// with the instruction it is actually in, and can end up stepping over a real call
    /// without ever seeing it.
    /// </para>
    /// <para>
    /// It went wrong the moment the GitHub client landed, and it went wrong silently: the
    /// host diff reader stopped appearing as a caller of <c>Process.Start</c>, which made
    /// <c>A_rounds_diff_is_generated_from_git_and_from_nothing_else</c> fail on a
    /// <em>missing</em> entry rather than on a wrong one — a reader that misses calls
    /// makes every claim in this file weaker, because "no second caller" and "this caller
    /// was not seen" are indistinguishable. Nothing else in the suite would have caught
    /// it: a check that only ever passes on an empty result is not a check.
    /// </para>
    /// <para>
    /// So the instruction lengths come from the runtime's own opcode table rather than from
    /// a guess. Every opcode is asked how many bytes it occupies, which is a table the
    /// runtime already has and cannot get wrong; the fallback for an opcode the table does
    /// not know is one byte, which is the shortest any instruction can be, so a future
    /// opcode can only make the scan re-read an operand rather than skip past a call.
    /// </para>
    /// </remarks>
    private static IEnumerable<int> Instructions(byte[] il)
    {
        for (var at = 0; at < il.Length;)
        {
            var op = il[at];
            var size = 1;
            int token;

            if (op == 0xFE)
            {
                // A two-byte opcode: 0xFE is a prefix, never an instruction of its own.
                if (at + 1 >= il.Length)
                {
                    yield break;
                }

                size = 2;
                token = 0xFE00 | il[at + 1];
            }
            else
            {
                token = op;
            }

            if (token == 0x45)
            {
                // `switch`, whose case table is a run of int32 offsets the length of which
                // is the operand itself. Decoded here because no fixed operand length is
                // right for it, and getting it wrong steps over everything after the jump.
                var cases = BitConverter.ToInt32(il, at + 1);
                at += size + 4 + (4 * cases);
                continue;
            }

            if (token is 0x28 or 0x6F or 0x73 && at + 4 < il.Length)
            {
                yield return at;
            }

            at += size + OperandBytes(token);
        }
    }

    /// <summary>
    /// How many bytes of operand each instruction carries, read off the runtime's own
    /// opcode table once. A switch's case table is variable-length and is handled where it
    /// is decoded rather than here, because no fixed answer is right for it.
    /// </summary>
    private static readonly Dictionary<int, int> OperandSizes = Build();

    private static Dictionary<int, int> Build()
    {
        var sizes = new Dictionary<int, int>();

        foreach (var field in typeof(System.Reflection.Emit.OpCodes).GetFields())
        {
            if (field.GetValue(null) is System.Reflection.Emit.OpCode op)
            {
                sizes[op.Value & 0xFFFF] = OperandLength(op.OperandType);
            }
        }

        return sizes;
    }

    /// <summary>
    /// How many bytes of operand an instruction of this shape carries. A token is four and
    /// a switch is four plus its case table; everything else is fixed, and the sizes come
    /// from the enum rather than from a table written out here.
    /// </summary>
    private static int OperandLength(System.Reflection.Emit.OperandType operand) => operand switch
    {
        System.Reflection.Emit.OperandType.InlineNone => 0,
        System.Reflection.Emit.OperandType.ShortInlineBrTarget or
        System.Reflection.Emit.OperandType.ShortInlineI or
        System.Reflection.Emit.OperandType.ShortInlineVar => 1,
        System.Reflection.Emit.OperandType.InlineVar => 2,
        System.Reflection.Emit.OperandType.InlineBrTarget or
        System.Reflection.Emit.OperandType.InlineField or
        System.Reflection.Emit.OperandType.InlineI or
        System.Reflection.Emit.OperandType.InlineMethod or
        System.Reflection.Emit.OperandType.InlineSig or
        System.Reflection.Emit.OperandType.InlineString or
        System.Reflection.Emit.OperandType.InlineTok or
        System.Reflection.Emit.OperandType.InlineType or
        System.Reflection.Emit.OperandType.ShortInlineR => 4,
        System.Reflection.Emit.OperandType.InlineI8 or
        System.Reflection.Emit.OperandType.InlineR => 8,
        System.Reflection.Emit.OperandType.InlineSwitch => 0,
        _ => 0,
    };

    private static int OperandBytes(int token) =>
        OperandSizes.TryGetValue((ushort)token, out var size) ? size : 0;
}

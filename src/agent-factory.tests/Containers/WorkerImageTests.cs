namespace AgentFactory.Tests.Containers;

using System.Text;
using AgentFactory.Containers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

/// <summary>
/// The worker image itself, driven by Testcontainers rather than by the factory's own
/// runtime. This is the layer a fake cannot reach and so is worth reaching with a
/// different tool: the image really starts, the toolchain it claims really is in it at
/// the versions it claims, the round machinery really runs, and a file really comes back
/// to this host.
/// </summary>
/// <remarks>
/// <para>
/// Testcontainers is used for the image and not for the factory's container lifecycle.
/// The lifecycle's mechanism is <c>docker cp</c> and <c>docker logs</c> — ADR-0010 names
/// those two verbs — and the honest way to cover that is through the component that uses
/// them, which is <see cref="WorkerRoundTests"/>. A container Testcontainers created is
/// here to answer a different question: what is in the image, and does the round protocol
/// work at all without the factory in the way.
/// </para>
/// <para>
/// No port is published and no host path is mounted on any container here either, which
/// is a property of how a container is asked for rather than of the image, so it is
/// asked for this way on purpose.
/// </para>
/// <para>
/// Skipped on a machine with no Docker daemon or no worker image; see
/// <see cref="DockerFactAttribute"/>.
/// </para>
/// </remarks>
[Collection("worker containers")]
public class WorkerImageTests
{
    [DockerFact]
    public async Task The_worker_image_starts_with_its_toolchain_present_and_nothing_listening()
    {
        await using var container = new ContainerBuilder(WorkerImage.Tag)
            // A container that is still up, so that what is in it can be asked about. A
            // round's own container is created and destroyed around one round; this one is
            // here to be looked inside.
            .WithCommand(["shell", "-c", "set -uo pipefail\nwhile true; do sleep 1; done"])
            .WithCleanUp(true)
            .Build();

        await container.StartAsync();

        // Unprivileged. Every one of these is read by running something inside the
        // container rather than by reading the Dockerfile that claims it.
        Assert.Equal("agent", (await container.ExecAsync(["id", "-un"])).Stdout.Trim());
        Assert.Equal("1000", (await container.ExecAsync(["id", "-u"])).Stdout.Trim());
        Assert.Equal("1000", (await container.ExecAsync(["id", "-G"])).Stdout.Trim());

        // The toolchain the round needs, at the versions the image's own table says.
        Assert.Contains("git version", (await container.ExecAsync(["git", "--version"])).Stdout, StringComparison.Ordinal);
        Assert.Contains("v2.", (await container.ExecAsync(["opencode", "--version"])).Stdout, StringComparison.Ordinal);
        Assert.Contains("4.", (await container.ExecAsync(["code-server", "--version"])).Stdout, StringComparison.Ordinal);

        // code-server is carried and never started, so there is no socket to reach inside
        // the container even if a deployment did publish one.
        var listening = await container.ExecAsync(
            ["bash", "-c", "awk 'NR>1 && $4==\"0A\"' /proc/net/tcp /proc/net/tcp6 | wc -l"]);

        Assert.Equal("0", listening.Stdout.Trim());
    }

    [DockerFact]
    public async Task A_commit_made_inside_the_worker_container_comes_back_to_this_host()
    {
        await using var container = new ContainerBuilder(WorkerImage.Tag)
            .WithCommand(["shell", "-c", "set -uo pipefail\nwhile true; do sleep 1; done"])
            .WithCleanUp(true)
            .Build();

        await container.StartAsync();

        // A real repository, a real commit, a real file. ADR-0006 needs the host to be
        // able to reach what a round committed, and this is the only way to find out
        // whether it can once the container is gone.
        var committed = await container.ExecAsync(
            [
                "bash",
                "-c",
                "git init -q -b main /tmp/round && git -C /tmp/round commit -q --allow-empty -m 'the round' "
                    + "&& git -C /tmp/round rev-parse HEAD",
            ]);

        Assert.Equal(0, committed.ExitCode);
        var head = committed.Stdout.Trim();
        Assert.Equal(40, head.Length);

        // The host reads the commit back as a file out of the container, with no mount and
        // no port involved in either direction.
        Assert.Equal(head, Text(await container.ReadFileAsync("/tmp/round/.git/refs/heads/main")).Trim());
        Assert.Contains("the round", Text(await container.ReadFileAsync("/tmp/round/.git/COMMIT_EDITMSG")), StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task A_round_runs_in_the_worker_image_and_its_result_file_comes_back_to_this_host()
    {
        await using var container = new ContainerBuilder(WorkerImage.Tag)
            .WithCommand(["shell", "-c", "set -uo pipefail\ngit init -q -b main /work\n"
                + "git -C /work commit -q --allow-empty -m 'the base'\n"
                + "printf 'the answer' > /work/answer.txt\n"
                + "git -C /work add -A && git -C /work commit -q -m 'the round'\n"])
            // Wait for the round to have ended, not for the container merely to be up: this
            // is a round's container, and the thing being asked is what the round left.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("worker-round: result="))
            .WithCleanUp(true)
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch (ContainerNotRunningException)
        {
            // A round's container is not a long-running one: it runs the round and exits,
            // and a fast round is gone before a wait strategy has looked at it. For this
            // container that is the round working rather than a failure to become ready,
            // so it is checked rather than assumed: it has to have exited cleanly, having
            // said where its result is.
            var logs = (await container.GetLogsAsync()).Stdout;
            Assert.Equal(0, await container.GetExitCodeAsync());
            Assert.Contains("worker-round: result=", logs, StringComparison.Ordinal);
        }

        // The image's own entrypoint ran the round and collected it: one result file, out
        // of the factory's directory rather than out of the working tree (ADR-0010).
        var result = Text(await container.ReadFileAsync(ContainerRuntime.ResultPathInContainer));

        Assert.StartsWith("{\"kind\":\"result\"", result, StringComparison.Ordinal);
        Assert.Contains("\"schema\":\"agent-factory/worker-result@1\"", result, StringComparison.Ordinal);
        Assert.Contains("\"uid\":1000", result, StringComparison.Ordinal);
        Assert.Contains("\"credentialEnvNames\":[]", result, StringComparison.Ordinal);
        Assert.Contains("\"gitRepo\":true", result, StringComparison.Ordinal);

        // The result never mentions its own paths. A result written inside the working
        // tree would show up in the diff the reviewer is being asked to judge, and the
        // image refuses to do that (ADR-0010).
        Assert.DoesNotContain(ContainerRuntime.ResultPathInContainer, result, StringComparison.Ordinal);

        // The round's commit and its change are both readable from this host, as files.
        // The commit matters because the host has to push it (ADR-0006); the change
        // matters because a diff is what a reviewer judges.
        Assert.Equal(40, Text(await container.ReadFileAsync("/work/.git/refs/heads/main")).Trim().Length);
        Assert.Equal("the round", Text(await container.ReadFileAsync("/work/.git/COMMIT_EDITMSG")).Trim());
        Assert.Equal("the answer", Text(await container.ReadFileAsync("/work/answer.txt")));

        // And the container's own log says how the round ended and where its result is, so
        // a host reading only the log still learns both.
        Assert.Contains(
            "worker-round: result=/out/result.json roundExitCode=0",
            (await container.GetLogsAsync()).Stdout,
            StringComparison.Ordinal);
    }

    private static string Text(byte[] file) => Encoding.UTF8.GetString(file);
}

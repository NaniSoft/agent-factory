namespace AgentFactory.Tests.Api;

using System.Net;
using System.Text.Json;
using AgentFactory;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The first JSON endpoint, driven over the process's real HTTP surface. The factory, the
/// loop and the view model are the real thing; only the agent is substituted, and nothing
/// here waits. What is asserted is exactly what a renderer reads: the container budget as
/// the loop is counting it against the one code constant, and whether silence can merge.
/// </summary>
public class BoardEndpointTests
{
    private const string Nexus = "https://github.com/NaniSoft/nexus";
    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task The_board_endpoint_reports_the_budget_and_the_auto_merge_mode()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root, autoMerge: true);

        using var response = await host.Board.GetAsync("/api/board");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var board = document.RootElement;

        // The budget is the real numbers: the loop's in-flight count, which is zero with an
        // idle machine, against the one code constant. Written out rather than read from the
        // constant, because a test that read it would prove only that the endpoint agrees
        // with itself.
        Assert.Equal(0, board.GetProperty("budget").GetProperty("inUse").GetInt32());
        Assert.Equal(2, board.GetProperty("budget").GetProperty("of").GetInt32());
        Assert.Equal(2, FactoryConstants.ContainerBudget);

        // The mode is the live option, not a default the endpoint chose.
        Assert.True(board.GetProperty("autoMerge").GetBoolean());
    }

    [Fact]
    public async Task The_budget_reads_a_round_the_loop_is_inside()
    {
        // The budget has to be the loop's own count and not a number the endpoint keeps, so
        // this holds two rounds open and reads the same endpoint the renderer reads. A copy
        // that never moved would return zero here and pass the test above.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Stuck().Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        await Take(host, 42);
        await Take(host, 43);
        await host.Settle();

        Assert.Equal(2, host.RoundsInFlight);

        using var response = await host.Board.GetAsync("/api/board");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(2, document.RootElement.GetProperty("budget").GetProperty("inUse").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("budget").GetProperty("of").GetInt32());
    }

    private static async Task<WorkItem> Take(FactoryHost host, int issueNumber)
    {
        var workItem = host.Store
            .Intake("nexus", Nexus, issueNumber, $"Issue {issueNumber}", IssueBody, "main")
            .WorkItem;
        await host.PromoteAsync(workItem.Id);
        return workItem;
    }
}

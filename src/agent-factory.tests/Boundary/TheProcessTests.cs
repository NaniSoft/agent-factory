namespace AgentFactory.Tests.Boundary;

using System.Net;

/// <summary>
/// The one process. It serves the board, it holds the store, and it is the whole
/// application boundary the rest of the suite drives.
/// </summary>
public class TheProcessTests
{
    [Fact]
    public async Task The_board_answers_from_the_one_process()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        var response = await host.Board.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

namespace AgentFactory.Tests.Boundary;

using System.Net;
using AgentFactory;
using Microsoft.Extensions.Configuration;

/// <summary>
/// The one process serves the board on port 5000, bound to loopback. Loopback is not a
/// default here: the machine is the trust boundary, and local access is the authorisation.
/// </summary>
[Collection("the board's port")]
public class BoardPortTests
{
    [Fact]
    public async Task One_process_serves_the_board_on_port_5000()
    {
        using var root = FactoryRoot.Create();

        // Options straight from configuration, exactly as the entry point derives them.
        var options = FactoryOptions.FromConfiguration(new ConfigurationBuilder().Build(), root.Path);
        await using var host = await FactoryHost.StartAsync(root, options);

        var response = await host.Board.GetAsync("/api/board");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://127.0.0.1:5000/", $"{host.BoardAddress}/");
    }
}

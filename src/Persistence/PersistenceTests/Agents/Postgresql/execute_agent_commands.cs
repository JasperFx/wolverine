using System.Runtime.CompilerServices;
using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using PersistenceTests.Marten;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Xunit;

namespace PersistenceTests.Agents.Postgresql;

public class execute_agent_commands : PostgresqlContext
{
    [Fact]
    public async Task run_agent_command_in_correct_queue()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var tracked = await host.SendMessageAndWaitAsync(new SimpleCommand());
        
        SimpleCommand.WasExecuted.ShouldBeTrue();
        
        tracked.Executed.SingleEnvelope<SimpleCommand>()
            .Destination.ShouldBe(new Uri("local://agents"));

        var runtime = host.GetRuntime();
        var agents = runtime.Endpoints.GetOrBuildSendingAgent(new Uri("local://agents"));

        var queue = agents.Endpoint.ShouldBeOfType<LocalQueue>();
        queue.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        queue.ExecutionOptions.MaxDegreeOfParallelism.ShouldBeGreaterThan(1);
        queue.ExecutionOptions.EnsureOrdered.ShouldBeFalse();
    }
}

public class SimpleCommand : IAgentCommand
{
    public static bool WasExecuted = false;
    
    async IAsyncEnumerable<object> IAgentCommand.ExecuteAsync(IWolverineRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        WasExecuted = true;
        yield break;
    }
}
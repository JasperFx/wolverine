using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Weasel.Postgresql;
using Wolverine.Postgresql;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests;

public class exclusive_listeners : IAsyncLifetime
{
    private readonly List<IHost> _hosts = [];
    private readonly ITestOutputHelper _output;
    private IHost _originalHost;

    public exclusive_listeners(ITestOutputHelper output)
    {
        _output = output;
    }

    public static async Task documentation_sample()
    {
        #region sample_utilizing_ListenWithStrictOrdering

        var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseRabbitMq().EnableWolverineControlQueues();
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "listeners");

            opts.ListenToRabbitQueue("ordered")

                // This option is available on all types of Wolverine
                // endpoints that can be configured to be a listener
                .ListenWithStrictOrdering();
        }).StartAsync();

        #endregion
    }

    [Fact]
    public async Task exclusive_listeners_are_automatically_started_in_solo_mode()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.ListenAtPort(PortFinder.GetAvailablePort()).ListenWithStrictOrdering().Named("one");
                opts.ListenAtPort(PortFinder.GetAvailablePort()).ListenWithStrictOrdering().Named("two");
                opts.ListenAtPort(PortFinder.GetAvailablePort()).Named("three");
            }).StartAsync();

        var runtime = host.GetRuntime();
        runtime.Endpoints.ActiveListeners().Select(x => x.Endpoint.EndpointName)
            .OrderBy(x => x)
            .ShouldHaveTheSameElementsAs("one", "three", "two");
    }

    public async Task InitializeAsync()
    {
        await dropSchema();

        _originalHost = await startHostAsync();
    }

    public async Task DisposeAsync()
    {
        _hosts.Reverse();
        foreach (var host in _hosts) await host.StopAsync();
    }

    private async Task<IHost> startHostAsync()
    {
        var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Durability.HealthCheckPollingTime = 1.Seconds();

            opts.Services.AddSingleton<IAgentFamily, FakeAgentFamily>();
            opts.UseRabbitMq().EnableWolverineControlQueues().AutoProvision();
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "listeners");
            opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

            opts.ListenToRabbitQueue("one").ListenWithStrictOrdering();
            opts.ListenToRabbitQueue("two").ListenWithStrictOrdering();
            opts.ListenToRabbitQueue("three").ListenWithStrictOrdering();

            opts.PublishMessage<ExclusiveMessage>().ToRabbitQueue("one");

            opts.Services.AddResourceSetupOnStartup();
        }).StartAsync();

        new XUnitEventObserver(host, _output);

        _hosts.Add(host);

        return host;
    }

    private async Task shutdownHostAsync(IHost host)
    {
        host.GetRuntime().Agents.DisableHealthChecks();
        await host.StopAsync();
        _hosts.Remove(host);
    }

    private static async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("registry");
        await conn.CloseAsync();
    }

    [Fact]
    public async Task spread_out_the_exclusive_listeners()
    {
        var host2 = await startHostAsync();
        var host3 = await startHostAsync();

        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = ExclusiveListenerFamily.SchemeName;
            w.ExpectRunningAgents(_originalHost, 1);
            w.ExpectRunningAgents(host2, 1);
            w.ExpectRunningAgents(host3, 1);

        }, 30.Seconds());

        var session = await _originalHost.TrackActivity().AlsoTrack(host2, host3)
            .SendMessageAndWaitAsync(new ExclusiveMessage());

        session.Received.SingleMessage<ExclusiveMessage>().ShouldNotBeNull();
    }
}

public class ExclusiveMessage;

public static class ExclusiveMessageHandler
{
    public static void Handle(ExclusiveMessage message)
    {
    }
}
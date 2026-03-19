using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace PolecatTests.Sagas;

public class using_revisioned_sagas : IAsyncLifetime
{
    private IHost theHost;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "revisioned_sagas";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await ((DocumentStore)theHost.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();
    }

    [Fact]
    public async Task execute_using_update_revision()
    {
        var id = Guid.NewGuid();

        // Start the new saga
        await theHost.InvokeMessageAndWaitAsync(new StartNewPcRevisionedSaga(id));

        var slow = new PcSlowMessage { Id = id };

        var execution = Task.Run(async () =>
        {
            await theHost.MessageBus().InvokeAsync(slow);
        });

        await PcRevisionedSaga.InSlowMessage.Task;
        await theHost.MessageBus().InvokeAsync(new PcCommand1(id));

        slow.Source.SetResult();

        await execution;
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }
}

public class PcRevisionedSaga : Wolverine.Saga
{
    public static void Configure(HandlerChain chain)
    {
        chain.ProcessingLogLevel = LogLevel.None;
        chain.SuccessLogLevel = LogLevel.None;
    }

    public static TaskCompletionSource InSlowMessage = new TaskCompletionSource();

    public static PcRevisionedSaga Start(StartNewPcRevisionedSaga command) =>
        new PcRevisionedSaga { Id = command.Id };

    public Guid Id { get; set; }

    public int Version { get; set; }

    public bool One { get; set; }
    public bool Two { get; set; }
    public bool Three { get; set; }
    public bool Four { get; set; }

    private void checkForCompletion()
    {
        if (One && Two && Three && Four)
        {
            MarkCompleted();
        }
    }

    public void Handle(PcCommand1 cmd)
    {
        One = true;
        checkForCompletion();
    }

    public void Handle(PcCommand2 cmd)
    {
        Two = true;
        checkForCompletion();
    }

    public void Handle(PcCommand3 cmd)
    {
        Three = true;
        checkForCompletion();
    }

    public void Handle(PcCommand4 cmd)
    {
        Four = true;
        checkForCompletion();
    }

    public Task Handle(PcSlowMessage message)
    {
        InSlowMessage.SetResult();
        return message.Source.Task;
    }
}

public record StartNewPcRevisionedSaga(Guid Id);

public class PcSlowMessage
{
    public readonly TaskCompletionSource Source = new();
    public Guid Id { get; set; }
}

public record PcCommand1(Guid Id);
public record PcCommand2(Guid Id);
public record PcCommand3(Guid Id);
public record PcCommand4(Guid Id);

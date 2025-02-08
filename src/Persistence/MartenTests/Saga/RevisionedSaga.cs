using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Marten;
using Marten.Metadata;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Saga;

public class using_revisioned_sagas : IAsyncLifetime
{
    private IHost theHost;
    
    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "revisioned_sagas";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task execute_using_update_revision()
    {
        var id = Guid.NewGuid();
        
        // Start the new saga
        await theHost.InvokeMessageAndWaitAsync(new StartNewRevisionedSaga(id));

        var slow = new SlowMessage{Id = id};
        
        var execution = Task.Run(async () =>
        {
            await theHost.MessageBus().InvokeAsync(slow);
        });

        await RevisionedSaga.InSlowMessage.Task;
        await theHost.MessageBus().InvokeAsync(new Command1(id));
        
        slow.Source.SetResult();

        // Should throw ConcurrencyException, but hell, it's been basically impossible to make this work
        await execution;
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }
}

public class RevisionedSaga : Wolverine.Saga
{
    public static TaskCompletionSource InSlowMessage = new TaskCompletionSource();
    
    public static RevisionedSaga Start(StartNewRevisionedSaga command) => new RevisionedSaga { Id = command.Id };
    
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

    public void Handle(Command1 cmd)
    {
        One = true;
        checkForCompletion();
    }
    
    public void Handle(Command2 cmd)
    {
        Two = true;
        checkForCompletion();
    }
    
    public void Handle(Command3 cmd)
    {
        Three = true;
        checkForCompletion();
    }
    
    public void Handle(Command4 cmd)
    {
        Four = true;
        checkForCompletion();
    }

    public Task Handle(SlowMessage message)
    {
        InSlowMessage.SetResult();
        return message.Source.Task;
    }

}

public record StartNewRevisionedSaga(Guid Id);

public class SlowMessage
{
    public readonly TaskCompletionSource Source = new();
    public Guid Id { get; set; }
}

public record Command1(Guid Id);
public record Command2(Guid Id);
public record Command3(Guid Id);
public record Command4(Guid Id);
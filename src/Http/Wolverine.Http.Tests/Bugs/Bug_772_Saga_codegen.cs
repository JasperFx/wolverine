using System.Diagnostics;
using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_772_Saga_codegen
{
    [Fact]
    public async Task can_compile_without_issue()
    {
        // Arrange -- and sorry, it's a bit of "Arrange" to get an IHost
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());

        builder.Services
            .AddMarten(options =>
            {
                options.Connection(Servers.PostgresConnectionString);
            })
            .UseLightweightSessions()
            .IntegrateWithWolverine();

        builder.Host.UseWolverine(options =>
        {
            options.Discovery.IncludeAssembly(GetType().Assembly);
            
            options.Policies.AutoApplyTransactions();
            options.Policies.UseDurableLocalQueues();
            options.Policies.UseDurableOutboxOnAllSendingEndpoints();
        });

        builder.Services.AddScoped<IDataService, DataService>();

        // This is using Alba, which uses WebApplicationFactory under the covers
        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        // Finally, the "Act"!
        var originalMessage = new BeginProcess(Guid.NewGuid());
        
        // This is a built in extension method to Wolverine to "wait" until
        // all activity triggered by this operation is completed
        var tracked = await host.InvokeMessageAndWaitAsync(originalMessage);
        
        // And now it's okay to do assertions....
        // This would have failed if there was 0 or many ContinueProcess messages
        var continueMessage = tracked.Executed.SingleMessage<ContinueProcess>();
        
        continueMessage.DataId.ShouldBe(originalMessage.DataId);

    }
}

public interface IDataService
{
    Task<RecordData> GetData(Guid messageDataId);
}

public class DataService : IDataService
{
    public Task<RecordData> GetData(Guid messageDataId)
    {
        var answer = new RecordData { MessageDataId = messageDataId, Data = Guid.NewGuid().ToString()};
        return Task.FromResult(answer);
    }
}

public class RecordData
{
    public Guid MessageDataId { get; set; }
    public string Data { get; set; }
}

public record BeginProcess(Guid DataId);

public record ContinueProcess(Guid SagaId, Guid DataId, string Data);

public static class BeginProcessMiddleware
{
    public static async Task<RecordData> LoadAsync(BeginProcess message, IDataService dataService)
    {
        return await dataService.GetData(message.DataId);
    }
    
    public static void Finally()
    {
        // ...
    }
}

public class LongProcessSaga : Saga
{
    public Guid Id { get; init; }
    
    [Middleware(typeof(BeginProcessMiddleware))]
    public static (LongProcessSaga, OutgoingMessages) Start(BeginProcess message, RecordData? sourceData = null)
    {
        var outgoingMessages = new OutgoingMessages();

        var saga = new LongProcessSaga
        {
            Id = message.DataId,
        };

        if (sourceData is not null)
        {
            outgoingMessages.Add(new ContinueProcess(saga.Id, message.DataId, sourceData.Data));
        }

        return (
            saga,
            outgoingMessages
        );
    }

    public void Handle(ContinueProcess process)
    {
        Continued = true;
    }

    public bool Continued { get; set; }
}
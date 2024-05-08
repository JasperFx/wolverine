using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Refit;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_582_erroneous_faliure_ack
{
    [Fact]
    public async Task do_not_send_failure_ack()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Services.AddScoped<IUserService, UserService>();

        builder.Services.AddMarten(Servers.PostgresConnectionString)
            .UseLightweightSessions()
            .IntegrateWithWolverine();
        
        builder.Services.AddRefitClient<ITestHttpClient>();
        
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(GetType().Assembly);
        });

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapPost("/", async (CreateItemCommand cmd, IMessageBus bus) => await bus.InvokeAsync<Guid>(cmd));
        });
        
        await host.Scenario(x =>
        {
            x.Post.Json(new CreateItemCommand{Name = "foo"}).ToUrl("/");
        });
    }
}

public class CreateItemCommand
{
    public string Name { get; set; }
}

public class ItemCreated
{
    public Guid Id { get; set; }
}

public class ItemDocument
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

public class ItemHandler
{
    public async Task<Guid> Handle(
        CreateItemCommand _,
        IDocumentSession session)
    {
        await Task.Delay(1000);

        session.Insert(new ItemDocument
        {
            Id = Guid.NewGuid(),
            Name = "Foo"
        });
        
        //Saving here, generates FailureAcknowledgementHandler error
        await session.SaveChangesAsync();
        
        return Guid.NewGuid();
    }
}
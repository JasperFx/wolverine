using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;

namespace MartenTests.Saga;

public class not_found_usage : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "invitations";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task try_to_call_handle_on_already_expired_invitation()
    {
        var id = Guid.NewGuid().ToString();

        await _host.InvokeMessageAndWaitAsync(new InvitationIssued { Id = id });
        await _host.InvokeMessageAndWaitAsync(new InvitationExpired( id));

        await using var query = _host.DocumentStore().LightweightSession();
        
        // Should be deleted at this point
        (await query.LoadAsync<InvitationPolicy>(id)).ShouldBeNull();
        
        // NotFound should fire here, and no exceptions
        await _host.InvokeMessageAndWaitAsync(new InvitationTimeout( id));
        
    }
}

public class InvitationPolicy : Wolverine.Saga
{
    public string Id { get; set; } = null!;

    public static (InvitationPolicy, InvitationTimeout) Start(InvitationIssued message)
    {
        return (new InvitationPolicy{Id = message.Id}, new InvitationTimeout(message.Id));
    }

    public InvitationExpired Handle(InvitationTimeout message, ILogger<InvitationPolicy> logger)
    {
        logger.LogInformation("Invitation with ID {Id} has timed out", message.Id);
        return new InvitationExpired(message.Id);
    }

    public void Handle(InvitationExpired message, ILogger<InvitationPolicy> logger)
    {
        logger.LogInformation("Completing saga with id {ID}", message.Id);
        MarkCompleted();
    }
    
    public void Handle(InvitationAccepted message, ILogger<InvitationPolicy> logger)
    {
        logger.LogInformation("Invitation has been accepted. Deleting saga with id {Id}", message.Id);
        MarkCompleted();
    }

    public static void NotFound(InvitationTimeout timeout, ILogger<InvitationPolicy> logger)
    {
        logger.LogError("Saga with {Id} has been completed already", timeout.Id);
    }
}

public record InvitationAccepted(string Id);
public record InvitationExpired(string Id);

public class InvitationIssued
{
    [SagaIdentity] public string Id { get; set; }
};

public record InvitationTimeout(string Id) : TimeoutMessage(10.Seconds());
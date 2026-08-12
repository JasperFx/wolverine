using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
///     The Wolverine-side proof of the Fisher defect found building this integration (fisher 0.5.3,
///     the same shape Polecat fixed in polecat#161).
/// </summary>
/// <remarks>
///     Wolverine's outbox writes its envelope rows through an <c>ITransactionParticipant</c> on Fisher's
///     own connection — that is what keeps a Fisher application to one writer per SQLite file. A handler
///     that only cascades a message writes no document and appends no event, so the participant is the
///     <b>only</b> work in that transaction. Fisher's "nothing queued, so nothing to do"
///     short-circuit did not look at participants, and the envelope was dropped silently: the cascade
///     looked successful and the message never existed.
/// </remarks>
public class outbox_with_an_empty_unit_of_work : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase(nameof(outbox_with_an_empty_unit_of_work));

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(CascadeOnlyHandler))
                    .IncludeType(typeof(TheCascadedMessageHandler));

                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddFisher(o =>
                    {
                        o.Connection(theDatabase.ConnectionString);
                        o.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task the_cascaded_message_survives_a_handler_that_writes_nothing_else()
    {
        TheCascadedMessageHandler.Received.Clear();

        var session = await theHost.InvokeMessageAndWaitAsync(new CascadeOnly("only a cascade"));

        session.Executed.SingleMessage<TheCascadedMessage>()
            .Name.ShouldBe("only a cascade");

        TheCascadedMessageHandler.Received.ShouldContain("only a cascade");
    }
}

public record CascadeOnly(string Name);

public record TheCascadedMessage(string Name);

public static class CascadeOnlyHandler
{
    // No document written, no event appended - the outbox participant is the entire unit of work
    public static TheCascadedMessage Handle(CascadeOnly command) => new(command.Name);
}

public static class TheCascadedMessageHandler
{
    public static List<string> Received { get; } = [];

    public static void Handle(TheCascadedMessage message) => Received.Add(message.Name);
}

using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace PostgresqlTests.Sagas;

// GH-5557: a message that fails in a saga handler is dead lettered with the saga id the handler resolved from
// the message, even though the incoming envelope carried none, so the saga's dead letters can be found by id
public class saga_dead_letters_carry_the_saga_id
{
    private const string SchemaName = "saga_dlq_5557";

    [Fact]
    public async Task a_failed_saga_message_is_found_by_its_saga_id()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync(SchemaName, TestContext.Current.CancellationToken);
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<DeadLetteringSaga5557>();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.Policies.UseDurableLocalQueues();
                opts.OnException<SagaStepFailed5557>().MoveToErrorQueue();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        var sagaId = Guid.NewGuid();
        var otherSagaId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartDeadLetteringSaga5557(sagaId));
        await host.InvokeMessageAndWaitAsync(new StartDeadLetteringSaga5557(otherSagaId));

        await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingSagaStep5557(sagaId));
        await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingSagaStep5557(otherSagaId));

        var deadLetters = host.GetRuntime().Storage.DeadLetters;
        var results = await deadLetters.QueryAsync(new DeadLetterEnvelopeQuery { SagaId = sagaId.ToString() },
            TestContext.Current.CancellationToken);

        var deadLetter = results.Envelopes.ShouldHaveSingleItem();
        deadLetter.SagaId.ShouldBe(sagaId.ToString());
        deadLetter.ExceptionType.ShouldBe(typeof(SagaStepFailed5557).FullName);
        deadLetter.Envelope.SagaId.ShouldBe(sagaId.ToString());
    }
}

public record StartDeadLetteringSaga5557(Guid Id);

public record FailingSagaStep5557(Guid DeadLetteringSaga5557Id);

public class SagaStepFailed5557() : Exception("The saga step failed");

public class DeadLetteringSaga5557 : Saga
{
    public Guid Id { get; set; }

    public static DeadLetteringSaga5557 Start(StartDeadLetteringSaga5557 command) => new() { Id = command.Id };

    public void Handle(FailingSagaStep5557 step) => throw new SagaStepFailed5557();
}

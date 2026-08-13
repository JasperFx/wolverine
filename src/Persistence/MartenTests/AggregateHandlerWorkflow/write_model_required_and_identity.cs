using IntegrationTests;
using JasperFx;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

// GH-3916 and GH-3918, both found adopting the GH-3907 attributes in a real consumer.
//
// GH-3916: [WriteModel].Required used to default to an unconditional true and ignore the parameter's
// nullable annotation, so `Account? account` still generated an EntityIsNotNullGuard -> Stop and the
// handler's own null branch became dead code, silently, with a logged warning per message.
//
// GH-3918: [WriteModel] resolved identity by name convention only, while [DeciderFunction] has always
// honored [Identity] on the command member. The same command against the same model needed an explicit
// [WriteModel("...")] under one form and nothing under the other.
public class write_model_required_and_identity : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(NullableModelHandler))
                    .IncludeType(typeof(ExplicitlyRequiredNullableModelHandler))
                    .IncludeType(typeof(NonNullableModelHandler))
                    .IncludeType(typeof(IdentityMarkedHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "write_model_required";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Guid> givenBalance(decimal opening)
    {
        var streamId = Guid.NewGuid();
        await using var session = theHost.DocumentStore().LightweightSession();
        session.Events.StartStream<Account>(streamId, new AmountDeposited(opening));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    // GH-3916
    [Fact]
    public async Task nullable_parameter_is_not_required_by_default()
    {
        NullableModelHandler.Reset();

        // Nothing has ever been written to this stream, so the model is null
        await theHost.InvokeAsync(new RecordDepositOnMaybeMissing(Guid.NewGuid(), 5m));

        NullableModelHandler.WasCalled.ShouldBeTrue();
        NullableModelHandler.SawNull.ShouldBeTrue();
    }

    // GH-3916: the annotation is a default, not an override. An explicit Required = true still wins.
    [Fact]
    public async Task explicit_required_still_wins_over_the_nullable_annotation()
    {
        ExplicitlyRequiredNullableModelHandler.Reset();

        await theHost.InvokeAsync(new RecordDepositRequiringModel(Guid.NewGuid(), 5m));

        ExplicitlyRequiredNullableModelHandler.WasCalled.ShouldBeFalse();
    }

    // GH-3916: a non-nullable parameter keeps behaving exactly as it did before
    [Fact]
    public async Task non_nullable_parameter_is_still_required()
    {
        NonNullableModelHandler.Reset();

        await theHost.InvokeAsync(new RecordDepositOnRequiredModel(Guid.NewGuid(), 5m));

        NonNullableModelHandler.WasCalled.ShouldBeFalse();

        // ...and runs normally when the model does exist
        await theHost.InvokeAsync(new RecordDepositOnRequiredModel(await givenBalance(10m), 5m));

        NonNullableModelHandler.WasCalled.ShouldBeTrue();
    }

    // GH-3918
    [Fact]
    public async Task write_model_honors_the_identity_attribute_on_the_command()
    {
        var streamId = await givenBalance(60m);

        // "theStreamId" matches none of [WriteModel]'s name conventions - accountId, id - so before
        // GH-3918 this chain could not resolve an identity at all
        await theHost.InvokeAsync(new WithdrawFromMarkedAccount(streamId, 10m));

        await using var session = theHost.DocumentStore().LightweightSession();
        var account = await session.Events.AggregateStreamAsync<Account>(streamId,
            token: TestContext.Current.CancellationToken);

        account!.Balance.ShouldBe(50m);
    }
}

public record RecordDepositOnMaybeMissing(Guid AccountId, decimal Amount);

public record RecordDepositRequiringModel(Guid AccountId, decimal Amount);

public record RecordDepositOnRequiredModel(Guid AccountId, decimal Amount);

public record WithdrawFromMarkedAccount([property: Identity] Guid TheStreamId, decimal Amount);

public static class NullableModelHandler
{
    public static bool WasCalled { get; private set; }
    public static bool SawNull { get; private set; }

    public static void Reset()
    {
        WasCalled = false;
        SawNull = false;
    }

    public static AmountDeposited Handle(RecordDepositOnMaybeMissing command, [WriteModel] Account? account)
    {
        WasCalled = true;
        SawNull = account == null;

        return new AmountDeposited(command.Amount);
    }
}

public static class ExplicitlyRequiredNullableModelHandler
{
    public static bool WasCalled { get; private set; }

    public static void Reset() => WasCalled = false;

    public static AmountDeposited Handle(RecordDepositRequiringModel command,
        [WriteModel(Required = true)] Account? account)
    {
        WasCalled = true;

        return new AmountDeposited(command.Amount);
    }
}

public static class NonNullableModelHandler
{
    public static bool WasCalled { get; private set; }

    public static void Reset() => WasCalled = false;

    public static AmountDeposited Handle(RecordDepositOnRequiredModel command, [WriteModel] Account account)
    {
        WasCalled = true;

        return new AmountDeposited(command.Amount);
    }
}

public static class IdentityMarkedHandler
{
    public static AmountWithdrawn Handle(WithdrawFromMarkedAccount command, [WriteModel] Account account)
        => new(command.Amount);
}

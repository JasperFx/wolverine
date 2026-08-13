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

// GH-3929. GH-3916 gave [WriteModel] a Required default taken from the parameter's nullable annotation
// and left [ReadModel] on an unconditional true, so the write side inferred and the read side did not.
//
// The part that needs pinning rather than fixing: [WriteAggregate] and [ReadAggregate] derive from the
// core attributes and predate the inference by a year. Inheriting it would silently drop the not-found
// guard from existing `[WriteAggregate] Account? account` handlers, which would then run against a model
// that was never loaded. Both keep their original unconditional default; the tests below are what say so.
public class read_model_required_3929 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(NullableReadModelHandler))
                    .IncludeType(typeof(ExplicitlyRequiredNullableReadModelHandler))
                    .IncludeType(typeof(NonNullableReadModelHandler))
                    .IncludeType(typeof(PinnedReadAggregateUsage))
                    .IncludeType(typeof(PinnedWriteAggregateUsage));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "read_model_required_3929";
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

    [Fact]
    public async Task nullable_read_model_is_not_required_by_default()
    {
        NullableReadModelHandler.Reset();

        // Nothing has ever been written to this stream, so the model is null
        await theHost.InvokeAsync(new ReadMaybeMissingAccount(Guid.NewGuid()));

        NullableReadModelHandler.WasCalled.ShouldBeTrue();
        NullableReadModelHandler.SawNull.ShouldBeTrue();
    }

    // The annotation is a default, not an override.
    [Fact]
    public async Task explicit_required_still_wins_over_the_nullable_annotation()
    {
        ExplicitlyRequiredNullableReadModelHandler.Reset();

        await theHost.InvokeAsync(new ReadRequiredAccount(Guid.NewGuid()));

        ExplicitlyRequiredNullableReadModelHandler.WasCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task non_nullable_read_model_is_still_required()
    {
        NonNullableReadModelHandler.Reset();

        await theHost.InvokeAsync(new ReadNonNullableAccount(Guid.NewGuid()));
        NonNullableReadModelHandler.WasCalled.ShouldBeFalse();

        await theHost.InvokeAsync(new ReadNonNullableAccount(await givenBalance(10m)));
        NonNullableReadModelHandler.WasCalled.ShouldBeTrue();
    }

    // REGRESSION GUARD. [ReadAggregate] must NOT inherit the inference - it shipped 2025-02-28 with an
    // unconditional Required, and quietly dropping the guard is a runtime-only behaviour change.
    [Fact]
    public async Task read_aggregate_keeps_its_unconditional_required_default()
    {
        PinnedReadAggregateUsage.Reset();

        await theHost.InvokeAsync(new ReadMaybeMissingViaAggregate(Guid.NewGuid()));

        PinnedReadAggregateUsage.WasCalled.ShouldBeFalse();
    }

    // REGRESSION GUARD, and the one that actually broke: [WriteAggregate] inherited GH-3916's inference
    // in 6.27.0 even though it shipped 2025-07-28. Nothing covered the implicit case, so CI was green.
    [Fact]
    public async Task write_aggregate_keeps_its_unconditional_required_default()
    {
        PinnedWriteAggregateUsage.Reset();

        await theHost.InvokeAsync(new WriteMaybeMissingViaAggregate(Guid.NewGuid(), 5m));

        PinnedWriteAggregateUsage.WasCalled.ShouldBeFalse();
    }
}

public record ReadMaybeMissingAccount(Guid AccountId);

public record ReadRequiredAccount(Guid AccountId);

public record ReadNonNullableAccount(Guid AccountId);

public record ReadMaybeMissingViaAggregate(Guid AccountId);

public record WriteMaybeMissingViaAggregate(Guid AccountId, decimal Amount);

public static class NullableReadModelHandler
{
    public static bool WasCalled { get; private set; }
    public static bool SawNull { get; private set; }

    public static void Reset()
    {
        WasCalled = false;
        SawNull = false;
    }

    public static void Handle(ReadMaybeMissingAccount command, [ReadModel] Account? account)
    {
        WasCalled = true;
        SawNull = account == null;
    }
}

public static class ExplicitlyRequiredNullableReadModelHandler
{
    public static bool WasCalled { get; private set; }

    public static void Reset() => WasCalled = false;

    public static void Handle(ReadRequiredAccount command, [ReadModel(Required = true)] Account? account)
    {
        WasCalled = true;
    }
}

public static class NonNullableReadModelHandler
{
    public static bool WasCalled { get; private set; }

    public static void Reset() => WasCalled = false;

    public static void Handle(ReadNonNullableAccount command, [ReadModel] Account account)
    {
        WasCalled = true;
    }
}

public static class PinnedReadAggregateUsage
{
    public static bool WasCalled { get; private set; }

    public static void Reset() => WasCalled = false;

    public static void Handle(ReadMaybeMissingViaAggregate command, [ReadAggregate] Account? account)
    {
        WasCalled = true;
    }
}

public static class PinnedWriteAggregateUsage
{
    public static bool WasCalled { get; private set; }

    public static void Reset() => WasCalled = false;

    public static AmountDeposited Handle(WriteMaybeMissingViaAggregate command,
        [WriteAggregate] Account? account)
    {
        WasCalled = true;

        return new AmountDeposited(command.Amount);
    }
}

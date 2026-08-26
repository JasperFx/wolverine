using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace CoreTests.Acceptance.EventModel4147;

// GH-4147 / GH-4152. The acceptance criteria for stamping provenance are precedence claims, so they are
// tested as precedence: "every role the Wolverine source emits is attributed as derived; a
// runtime-observed source can override a derived role; an overlay cannot."
//
// ⚠️ Worth knowing why this file exists at all rather than leaning on the GH-3988 overlay fixture. The
// public overlay API (EventModelSliceBuilder) can only express TriggeredBy / InDomain /
// LinksToSpecification / Hotspot -- annotations, none of which are factual roles. So an overlay
// *cannot* collide with a derived role, and that fixture passes with or without the Derived stamp: it
// never exercises the ladder. Only a custom IEventModelDefinitionSource can claim a role Wolverine also
// claims, which is what these stubs do.
public class event_model_provenance_ladder_4147
{
    private static async Task<EventModelDescriptor> assembleWithAsync(params IEventModelDefinitionSource[] rivals)
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Registered BEFORE UseWolverine() so registration order favours the rival. Precedence
                // must come off the ladder now, not off the old services.Insert(0, ...) hack.
                foreach (var rival in rivals)
                {
                    services.AddSingleton(rival);
                }
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "provenance-4147";
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(RecordPaymentHandler));
            }).StartAsync();

        var model = await WolverineEventModelExport.AssembleAsync(host.Services,
            token: TestContext.Current.CancellationToken);

        await host.StopAsync();
        return model;
    }

    [Fact]
    public async Task an_observed_source_overrides_a_derived_role()
    {
        var model = await assembleWithAsync(
            new StubSource(EventModelProvenance.Observed, typeof(ObservedInProduction)));

        // The inversion #4147 asks for, and it is deliberate: what the fleet actually emits beats what
        // the code says it should emit.
        model.Slices.Single(x => x.Name == nameof(RecordPayment))
            .PublishedMessages.Select(x => x.Name)
            .ShouldBe([nameof(ObservedInProduction)]);
    }

    [Fact]
    public async Task a_declared_source_cannot_override_a_derived_role()
    {
        var model = await assembleWithAsync(
            new StubSource(EventModelProvenance.Declared, typeof(MerelyDeclared)));

        // Registered first, and still loses -- this is exactly what services.Insert(0, ...) used to buy
        // and what the Derived stamp buys now.
        model.Slices.Single(x => x.Name == nameof(RecordPayment))
            .PublishedMessages.Select(x => x.Name)
            .ShouldBe([nameof(PaymentRecorded)]);
    }

    [Fact]
    public async Task the_derived_role_is_what_wolverine_actually_compiled()
    {
        var model = await assembleWithAsync();

        model.Slices.Single(x => x.Name == nameof(RecordPayment))
            .PublishedMessages.Select(x => x.Name)
            .ShouldBe([nameof(PaymentRecorded)]);
    }

    /// <summary>
    ///     A rival source claiming the same slice's PublishedMessages at a chosen rung.
    /// </summary>
    private sealed class StubSource(EventModelProvenance provenance, Type published) : IEventModelDefinitionSource
    {
        public Uri Subject { get; } = new("event-model://stub-4147");

        public EventModelProvenance Provenance => provenance;

        public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
        {
            var slice = new EventModelSliceDescriptor(
                nameof(RecordPayment), null, null, null, null,
                Array.Empty<TypeDescriptor>(), Array.Empty<TypeDescriptor>(), Array.Empty<TypeDescriptor>())
            {
                PublishedMessages = [TypeDescriptor.For(published)]
            };

            return Task.FromResult<EventModelDescriptor?>(
                new EventModelDescriptor("provenance-4147", [slice]));
        }
    }
}

public record RecordPayment(string Id);

public record PaymentRecorded(string Id);

public record ObservedInProduction(string Id);

public record MerelyDeclared(string Id);

public class RecordPaymentHandler
{
    public PaymentRecorded Handle(RecordPayment command) => new(command.Id);
}

using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events.Aggregation;
using Shouldly;
using StronglyTypedIds;
using Wolverine.Configuration;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace PolecatTests.AggregateHandlerWorkflow;

public class AggregateHandlerAttributeTests
{
    [Fact]
    public void determine_version_member_for_aggregate()
    {
        AggregateHandling.DetermineVersionMember(typeof(PcInvoice))
            .Name.ShouldBe(nameof(PcInvoice.Version));
    }

    [Fact]
    public void determine_aggregate_by_second_parameter()
    {
        var chain = HandlerChain.For<PcInvoiceHandler>(x => x.Handle(default(ApprovePcInvoice), default),
            new HandlerGraph());
        AggregateHandling.DetermineAggregateType(chain)
            .ShouldBe(typeof(PcInvoice));
    }

    [Fact]
    public void throw_if_aggregate_type_is_indeterminate()
    {
        var chain = HandlerChain.For<PcInvoiceHandler>(x => x.Handle(default(ApprovePcInvoice)), new HandlerGraph());
        Should.Throw<InvalidOperationException>(() =>
        {
            AggregateHandling.DetermineAggregateType(chain);
        });
    }

    [Fact]
    public void throw_if_return_is_void_and_does_not_take_in_stream()
    {
        var chain = HandlerChain.For<PcInvoiceHandler>(x => x.Handle(default(PcInvalid1), default), new HandlerGraph());
        Should.Throw<InvalidOperationException>(() =>
        {
            new AggregateHandlerAttribute().Modify(chain, new GenerationRules(), ServiceContainer.Empty());
        });
    }

    [Fact]
    public void throw_if_return_is_Task_and_does_not_take_in_stream()
    {
        var chain = HandlerChain.For<PcInvoiceHandler>(x => x.Handle(default(PcInvalid2), default), new HandlerGraph());
        Should.Throw<InvalidOperationException>(() =>
        {
            new AggregateHandlerAttribute().Modify(chain, new GenerationRules(), ServiceContainer.Empty());
        });
    }

    [Fact]
    public void determine_aggregate_id_from_command_type_in_aggregate_handler_attribute()
    {
        var chain = HandlerChain.For<PcInvoiceHandler>(x => x.Handle(default(CreatePcInvoice)!), new HandlerGraph());
        new AggregateHandlerAttribute { AggregateType = typeof(PcInvoice) }.TryInferMessageIdentity(chain, out var property)
            .ShouldBe(true);
        property!.Name.ShouldBe(nameof(CreatePcInvoice.Id));
    }

    [Fact]
    public void determine_aggregate_id_from_command_type()
    {
        AggregateHandling.DetermineAggregateIdMember(typeof(PcInvoice), typeof(ApprovePcInvoice))
            .Name.ShouldBe(nameof(ApprovePcInvoice.PcInvoiceId));
    }

    [Fact]
    public void determine_aggregate_id_with_identity_attribute_help()
    {
        AggregateHandling.DetermineAggregateIdMember(typeof(PcInvoice), typeof(RejectPcInvoice))
            .Name.ShouldBe(nameof(RejectPcInvoice.Something));
    }

    [Fact]
    public void determine_aggregate_id_with_identity_attribute_bypass()
    {
        AggregateHandling.DetermineAggregateIdMember(typeof(PcInvoice), typeof(PcAggregateIdConventionBypassingCommand))
            .Name.ShouldBe(nameof(PcAggregateIdConventionBypassingCommand.StreamId));
    }

    [Fact]
    public void cannot_determine_aggregate_id()
    {
        Should.Throw<InvalidOperationException>(() =>
        {
            AggregateHandling.DetermineAggregateIdMember(typeof(PcInvoice), typeof(PcBadCommand));
        });
    }

    [Fact]
    public void determine_aggregate_id_by_strong_typed_id_on_aggregate_id_property()
    {
        AggregateHandling.DetermineAggregateIdMember(typeof(PcCourseAggregate), typeof(ChangePcCourseCapacity))
            .Name.ShouldBe(nameof(ChangePcCourseCapacity.PcCourseId));
    }

    [Fact]
    public void determine_aggregate_id_by_identified_by_interface()
    {
        AggregateHandling.DetermineAggregateIdMember(typeof(PcCourseAggregateWithInterface), typeof(ChangePcCourseCapacityForInterface))
            .Name.ShouldBe(nameof(ChangePcCourseCapacityForInterface.PcCourseId));
    }

    [Fact]
    public void strong_typed_id_matching_requires_single_property()
    {
        Should.Throw<InvalidOperationException>(() =>
        {
            AggregateHandling.DetermineAggregateIdMember(typeof(PcCourseAggregate), typeof(TransferBetweenPcCourses));
        });
    }

    [Fact]
    public void strong_typed_id_not_used_when_conventional_name_matches()
    {
        AggregateHandling.DetermineAggregateIdMember(typeof(PcCourseAggregate), typeof(UpdatePcCourseWithConventionalId))
            .Name.ShouldBe(nameof(UpdatePcCourseWithConventionalId.Id));
    }
}

public class PcInvoice
{
    public Guid Id { get; set; }
    public int Version { get; set; }
}

public record PcAggregateIdConventionBypassingCommand(Guid Id, [property: Identity] Guid StreamId);

public record PcBadCommand(Guid XId);

public record PcInvoiceApproved;

public record ApprovePcInvoice(Guid PcInvoiceId);

public record CreatePcInvoice(Guid Id);

public record PcInvoiceCreated;

public record RejectPcInvoice([property: Identity] Guid Something);

public class PcInvoiceHandler
{
    public PcInvoiceCreated Handle(CreatePcInvoice command)
    {
        return new PcInvoiceCreated();
    }

    public PcInvoiceApproved Handle(ApprovePcInvoice command, PcInvoice invoice)
    {
        return new PcInvoiceApproved();
    }

    public PcInvoiceApproved Handle(ApprovePcInvoice command)
    {
        return new PcInvoiceApproved();
    }

    public void Handle(PcInvalid1 command, PcInvoice invoice)
    {
    }

    public Task Handle(PcInvalid2 command, PcInvoice invoice)
    {
        return Task.CompletedTask;
    }
}

public record PcInvalid1(Guid PcInvoiceId);

public record PcInvalid2(Guid PcInvoiceId);

[StronglyTypedId(Template.Guid)]
public readonly partial struct PcCourseId;

public class PcCourseAggregate
{
    public PcCourseId Id { get; set; }
    public int Capacity { get; set; }
    public int Version { get; set; }

    public void Apply(PcCourseCapacityChanged e)
    {
        Capacity = e.NewCapacity;
    }
}

public class PcCourseAggregateWithInterface : IdentifiedBy<PcCourseId>
{
    public Guid Id { get; set; }
    public int Capacity { get; set; }
    public int Version { get; set; }
}

public record PcCourseCapacityChanged(int NewCapacity);

public record ChangePcCourseCapacity(PcCourseId PcCourseId, int NewCapacity);

public record ChangePcCourseCapacityForInterface(PcCourseId PcCourseId, int NewCapacity);

public record TransferBetweenPcCourses(PcCourseId SourcePcCourseId, PcCourseId TargetPcCourseId);

public record UpdatePcCourseWithConventionalId(PcCourseId Id, string Name);

using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Marten.Schema;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace MartenTests.AggregateHandlerWorkflow;

public class AggregateHandlerAttributeTests
{
    [Fact]
    public void determine_version_member_for_aggregate()
    {
        AggregateHandling.DetermineVersionMember(typeof(Invoice))
            .Name.ShouldBe(nameof(Invoice.Version));
    }

    [Fact]
    public void determine_aggregate_by_second_parameter()
    {
        var chain = HandlerChain.For<InvoiceHandler>(x => x.Handle(default(ApproveInvoice), default),
            new HandlerGraph());
        AggregateHandling.DetermineAggregateType(chain)
            .ShouldBe(typeof(Invoice));
    }

    [Fact]
    public void throw_if_aggregate_type_is_indeterminate()
    {
        var chain = HandlerChain.For<InvoiceHandler>(x => x.Handle(default(ApproveInvoice)), new HandlerGraph());
        Should.Throw<InvalidOperationException>(() =>
        {
            AggregateHandling.DetermineAggregateType(chain);
        });
    }

    [Fact]
    public void throw_if_return_is_void_and_does_not_take_in_stream()
    {
        var chain = HandlerChain.For<InvoiceHandler>(x => x.Handle(default(Invalid1), default), new HandlerGraph());
        Should.Throw<InvalidOperationException>(() =>
        {
            new AggregateHandlerAttribute().Modify(chain, new GenerationRules(), ServiceContainer.Empty());
        });
    }

    [Fact]
    public void throw_if_return_is_Task_and_does_not_take_in_stream()
    {
        var chain = HandlerChain.For<InvoiceHandler>(x => x.Handle(default(Invalid2), default), new HandlerGraph());
        Should.Throw<InvalidOperationException>(() =>
        {
            new AggregateHandlerAttribute().Modify(chain, new GenerationRules(), ServiceContainer.Empty());
        });
    }

    [Fact]
    public void determine_aggregate_id_from_command_type_in_aggregate_handler_attribute()
    {
        var chain = HandlerChain.For<InvoiceHandler>(x => x.Handle(default(CreateInvoice)), new HandlerGraph());
        new AggregateHandlerAttribute {AggregateType = typeof(Invoice) }.TryInferMessageIdentity(chain, out var property)
            .ShouldBe(true);
        property.Name.ShouldBe(nameof(CreateInvoice.Id));
    }
    
    [Fact]
    public void determine_aggregate_id_from_command_type()
    {
        AggregateHandling.DetermineAggregateIdMember(typeof(Invoice), typeof(ApproveInvoice))
            .Name.ShouldBe(nameof(ApproveInvoice.InvoiceId));
    }

    [Fact]
    public void determine_aggregate_id_with_identity_attribute_help()
    {
        AggregateHandling.DetermineAggregateIdMember(typeof(Invoice), typeof(RejectInvoice))
            .Name.ShouldBe(nameof(RejectInvoice.Something));
    }

    [Fact]
    public void determine_aggregate_id_with_identity_attribute_bypass()
    {
        AggregateHandling.DetermineAggregateIdMember(typeof(Invoice), typeof(AggregateIdConventionBypassingCommand))
            .Name.ShouldBe(nameof(AggregateIdConventionBypassingCommand.StreamId));
    }

    [Fact]
    public void cannot_determine_aggregate_id()
    {
        Should.Throw<InvalidOperationException>(() =>
        {
            AggregateHandling.DetermineAggregateIdMember(typeof(Invoice), typeof(BadCommand));
        });
    }
}

public class Invoice
{
    public Guid Id { get; set; }
    public int Version { get; set; }
}

public record AggregateIdConventionBypassingCommand(Guid Id, [property: Identity] Guid StreamId);

public record BadCommand(Guid XId);

public record InvoiceApproved;

public record ApproveInvoice(Guid InvoiceId);

public record CreateInvoice(Guid Id);

public record InvoiceCreated;

public record RejectInvoice([property: Identity] Guid Something);

public class InvoiceHandler
{
    public InvoiceCreated Handle(CreateInvoice command)
    {
        return new InvoiceCreated();
    }
    
    public InvoiceApproved Handle(ApproveInvoice command, Invoice invoice)
    {
        return new InvoiceApproved();
    }

    public InvoiceApproved Handle(ApproveInvoice command)
    {
        return new InvoiceApproved();
    }

    public void Handle(Invalid1 command, Invoice invoice)
    {
    }

    public Task Handle(Invalid2 command, Invoice invoice)
    {
        return Task.CompletedTask;
    }
}

public record Invalid1(Guid InvoiceId);

public record Invalid2(Guid InvoiceId);
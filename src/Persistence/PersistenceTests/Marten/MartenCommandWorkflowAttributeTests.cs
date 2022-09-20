using System;
using System.Threading.Tasks;
using Lamar;
using LamarCodeGeneration;
using Marten.Schema;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Wolverine.Persistence.Testing.Marten;

public class MartenCommandWorkflowAttributeTests
{
    [Fact]
    public void determine_version_member_for_aggregate()
    {
        var att = new MartenCommandWorkflowAttribute();
        att.DetermineVersionMember(typeof(Invoice))
            .Name.ShouldBe(nameof(Invoice.Version));
    }

    [Fact]
    public void determine_aggregate_type_when_it_is_explicitly_passed_in()
    {
        new MartenCommandWorkflowAttribute { AggregateType = typeof(Invoice) }
            .DetermineAggregateType(Substitute.For<IChain>())
            .ShouldBe(typeof(Invoice));
    }

    [Fact]
    public void determine_aggregate_by_second_parameter()
    {
        var chain = HandlerChain.For<InvoiceHandler>(x => x.Handle(default(ApproveInvoice), default),
            new HandlerGraph());
        new MartenCommandWorkflowAttribute().DetermineAggregateType(chain)
            .ShouldBe(typeof(Invoice));
    }

    [Fact]
    public void throw_if_aggregate_type_is_indeterminate()
    {
        var chain = HandlerChain.For<InvoiceHandler>(x => x.Handle(default), new HandlerGraph());
        Should.Throw<InvalidOperationException>(() =>
        {
            new MartenCommandWorkflowAttribute().DetermineAggregateType(chain);
        });
    }

    [Fact]
    public void throw_if_return_is_void_and_does_not_take_in_stream()
    {
        var chain = HandlerChain.For<InvoiceHandler>(x => x.Handle(default(Invalid1), default), new HandlerGraph());
        Should.Throw<InvalidOperationException>(() =>
        {
            new MartenCommandWorkflowAttribute().Modify(chain, new GenerationRules(), Container.Empty());
        });
    }

    [Fact]
    public void throw_if_return_is_Task_and_does_not_take_in_stream()
    {
        var chain = HandlerChain.For<InvoiceHandler>(x => x.Handle(default(Invalid2), default), new HandlerGraph());
        Should.Throw<InvalidOperationException>(() =>
        {
            new MartenCommandWorkflowAttribute().Modify(chain, new GenerationRules(), Container.Empty());
        });
    }

    [Fact]
    public void determine_aggregate_id_from_command_type()
    {
        MartenCommandWorkflowAttribute.DetermineAggregateIdMember(typeof(Invoice), typeof(ApproveInvoice))
            .Name.ShouldBe(nameof(ApproveInvoice.InvoiceId));
    }

    [Fact]
    public void determine_aggregate_id_with_identity_attribute_help()
    {
        MartenCommandWorkflowAttribute.DetermineAggregateIdMember(typeof(Invoice), typeof(RejectInvoice))
            .Name.ShouldBe(nameof(RejectInvoice.Something));
    }

    [Fact]
    public void cannot_determine_aggregate_id()
    {
        Should.Throw<InvalidOperationException>(() =>
        {
            MartenCommandWorkflowAttribute.DetermineAggregateIdMember(typeof(Invoice), typeof(BadCommand));
        });
    }
}

public class Invoice
{
    public Guid Id { get; set; }
    public int Version { get; set; }
}

public record BadCommand(Guid Id);

public record InvoiceApproved;

public record ApproveInvoice(Guid InvoiceId);

public record RejectInvoice([property: Identity] Guid Something);

public class InvoiceHandler
{
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

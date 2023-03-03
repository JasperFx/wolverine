using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Xunit;

namespace CoreTests.Configuration;

public class auditing_determination : IntegrationContext
{
    public auditing_determination(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public void finds_audit_members_from_attributes()
    {
        var chain = chainFor<AuditedMessage>();
        
        chain.AuditedMembers.Single(x => x.Member.Name == nameof(AuditedMessage.Name))
            .Heading.ShouldBe(nameof(AuditedMessage.Name));
        
        chain.AuditedMembers.Single(x => x.Member.Name == nameof(AuditedMessage.AccountId))
            .Heading.ShouldBe("AccountIdentifier");
    }

    [Fact]
    public void use_audit_members_from_explicit_interface_adds()
    {
        with(opts =>
        {
            opts.Policies.Audit<IAccountMessage>(x => x.AccountId);
        });
        
        var chain = chainFor<DebitAccount>();
        chain.AuditedMembers.Single().Member.Name.ShouldBe(nameof(IAccountMessage.AccountId));
    }
}

public class AuditedMessage
{
    [Audit]
    public string Name { get; set; }

    [Audit("AccountIdentifier")] public int AccountId;
}

public class AuditedHandler
{
    public void Handle(AuditedMessage message) => Console.WriteLine("Hello");

    public void Handle(DebitAccount message) => Console.WriteLine("Got a debit");
}

// Marker interface
public interface IAccountMessage
{
    public int AccountId { get; }
}

public record DebitAccount(int AccountId, decimal Amount) : IAccountMessage;
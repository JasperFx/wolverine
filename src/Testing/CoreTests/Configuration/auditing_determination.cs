using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Tracking;
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
    public void adds_the_audit_to_activity_code()
    {
        var chain = chainFor<AuditedMessage>();
        var lines = chain.SourceCode.ReadLines();

        lines.Any(x => x.Contains("Activity.Current?.SetTag(\"Name\", auditedMessage.Name)")).ShouldBeTrue();
        lines.Any(x => x.Contains("Activity.Current?.SetTag(\"AccountId\", auditedMessage.AccountId)")).ShouldBeTrue();
    }

    [Fact]
    public void adds_the_log_start_message_to_code()
    {
        with(opts =>
        {
            opts.Policies.LogMessageStarting(LogLevel.Information);
        });
        
        var chain = chainFor<AuditedMessage>();
        var lines = chain.SourceCode.ReadLines();

        var expected = "Log(Microsoft.Extensions.Logging.LogLevel.Information, \"Starting to process CoreTests.Configuration.AuditedMessage ({Id}) with Name: {Name}, AccountIdentifier: {AccountId}\", context.Envelope.Id, auditedMessage.Name, auditedMessage.AccountId)";
        
        lines.Any(x => x.Contains(expected)).ShouldBeTrue();
    }

    [Fact]
    public async Task execute_to_prove_it_does_not_blow_up()
    {
        with(opts =>
        {
            opts.Policies.LogMessageStarting(LogLevel.Information);
        });
        
        await Host.InvokeMessageAndWaitAsync(new AuditedMessage());
    }

    [Fact]
    public void use_audit_members_from_explicit_interface_adds()
    {
        with(opts =>
        {
            #region sample_explicit_registration_of_audit_properties

            // opts is WolverineOptions inside of a UseWolverine() call
            opts.Policies.Audit<IAccountMessage>(x => x.AccountId);

            #endregion
        });
        
        var chain = chainFor<DebitAccount>();
        chain.AuditedMembers.Single().Member.Name.ShouldBe(nameof(IAccountMessage.AccountId));
    }
}

#region sample_using_audit_attribute

public class AuditedMessage
{
    [Audit]
    public string Name { get; set; }

    [Audit("AccountIdentifier")] public int AccountId;
}

#endregion

public class AuditedHandler
{
    public void Handle(AuditedMessage message) => Console.WriteLine("Hello");

    public void Handle(DebitAccount message, ILogger logger, Envelope envelope)
    {
        logger.Log(LogLevel.Information, "Starting to process DebitAccount ({Id}) with AccountId {AccountId}", envelope.Id, message.Amount);
        
        var activity = Activity.Current;
        if (activity != null)
        {
            activity.SetTag(nameof(DebitAccount.AccountId), message.AccountId);
        }
    }
}

#region sample_account_message_for_auditing

// Marker interface
public interface IAccountMessage
{
    public int AccountId { get; }
}

// A possible command that uses our marker interface above
public record DebitAccount(int AccountId, decimal Amount) : IAccountMessage;

#endregion
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime.Deduplication;
using Xunit;

namespace CoreTests.Runtime.Deduplication;

// GH-4180 follow up. Envelope.DeduplicationId is the LOGICAL identity of a message, and until now
// the only way to set one was DeliveryOptions at every publishing call site. These cover deriving it
// from the message itself the way a topic name or a saga id already is.
public class deriving_the_deduplication_id_from_a_message
{
    private static async Task<Envelope> publish(object message, Action<WolverineOptions>? configure = null,
        DeliveryOptions? options = null)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().ToLocalQueue("deduplication");
                configure?.Invoke(opts);
            }).StartAsync(TestContext.Current.CancellationToken);

        var bus = host.MessageBus();

        return options == null
            ? bus.PreviewSubscriptions(message).Single()
            : bus.PreviewSubscriptions(message, options).Single();
    }

    [Fact]
    public async Task no_identity_declared_means_no_deduplication_id()
    {
        var envelope = await publish(new PlainMessage("nothing to see here"));
        envelope.DeduplicationId.ShouldBeNull();
    }

    [Fact]
    public async Task derive_from_a_member_marked_with_the_attribute()
    {
        var envelope = await publish(new MarkedMemberMessage("nightly|2026-08-30T03:00:00Z", "Orders"));
        envelope.DeduplicationId.ShouldBe("nightly|2026-08-30T03:00:00Z");
    }

    [Fact]
    public async Task derive_from_a_member_named_by_a_type_level_attribute()
    {
        var envelope = await publish(new NamedMemberMessage("Orders", "occurrence-1"));
        envelope.DeduplicationId.ShouldBe("occurrence-1");
    }

    // The identity member does not have to be a string -- an operator reading the deduplication
    // table wants something legible, and ToString() is what makes a Guid or a DateTimeOffset legible.
    [Fact]
    public async Task a_non_string_identity_member_is_converted()
    {
        var id = Guid.NewGuid();
        var envelope = await publish(new GuidIdentityMessage(id));
        envelope.DeduplicationId.ShouldBe(id.ToString());
    }

    [Fact]
    public async Task a_null_identity_member_leaves_the_id_alone()
    {
        var envelope = await publish(new MarkedMemberMessage(null!, "Orders"));
        envelope.DeduplicationId.ShouldBeNull();
    }

    [Fact]
    public async Task derive_from_a_configured_lambda()
    {
        var envelope = await publish(new PlainMessage("Orders"),
            opts => opts.MessageDeduplication.ByMessage<PlainMessage>(x => $"plain|{x.Name}"));

        envelope.DeduplicationId.ShouldBe("plain|Orders");
    }

    [Fact]
    public async Task derive_from_a_configured_lambda_through_message_type_policies()
    {
        var envelope = await publish(new PlainMessage("Orders"),
            opts => opts.Policies.ForMessagesOfType<PlainMessage>().DeduplicateBy(x => $"policy|{x.Name}"));

        envelope.DeduplicationId.ShouldBe("policy|Orders");
    }

    // A lambda that opts a particular message out has to leave the id empty rather than stamping
    // an empty string, or a [Deduplicated(Required = true)] handler would see a present-but-useless id
    [Fact]
    public async Task a_lambda_returning_null_leaves_the_id_alone()
    {
        var envelope = await publish(new PlainMessage("Orders"),
            opts => opts.MessageDeduplication.ByMessage<PlainMessage>(_ => null));

        envelope.DeduplicationId.ShouldBeNull();
    }

    [Fact]
    public async Task a_lambda_registered_for_a_base_type_applies_to_the_subclass()
    {
        var envelope = await publish(new SpecificMessage { Key = "abc" },
            opts => opts.MessageDeduplication.ByMessage<IHaveAKey>(x => x.Key));

        envelope.DeduplicationId.ShouldBe("abc");
    }

    [Fact]
    public async Task derive_by_member_name()
    {
        var envelope = await publish(new PlainMessage("Orders"),
            opts => opts.MessageDeduplication.ByMemberNamed("Nope", "Name"));

        envelope.DeduplicationId.ShouldBe("Orders");
    }

    // Matches() is asked once per message type at routing-compile time, so a message type without any
    // of the named members has to route cleanly rather than throwing when the rule is built
    [Fact]
    public async Task derive_by_member_name_ignores_a_message_type_without_the_member()
    {
        var envelope = await publish(new NoMatchingMemberMessage("Orders"),
            opts => opts.MessageDeduplication.ByMemberNamed("IdempotencyKey"));

        envelope.DeduplicationId.ShouldBeNull();
    }

    // Precedence. The publisher's explicit intent has to be the last word, or a message contract that
    // grew a [DeduplicationIdentity] would silently start overriding call sites that already set one.
    [Fact]
    public async Task explicit_delivery_options_win_over_the_attribute()
    {
        var envelope = await publish(new MarkedMemberMessage("from-the-message", "Orders"),
            options: new DeliveryOptions { DeduplicationId = "from-the-caller" });

        envelope.DeduplicationId.ShouldBe("from-the-caller");
    }

    [Fact]
    public async Task explicit_delivery_options_win_over_a_configured_lambda()
    {
        var envelope = await publish(new PlainMessage("Orders"),
            opts => opts.MessageDeduplication.ByMessage<PlainMessage>(x => x.Name),
            new DeliveryOptions { DeduplicationId = "from-the-caller" });

        envelope.DeduplicationId.ShouldBe("from-the-caller");
    }

    // An application's own configuration is the more local statement of intent, so it beats an
    // attribute baked into a contract the application merely consumes
    [Fact]
    public async Task configured_rules_win_over_the_attribute()
    {
        var envelope = await publish(new MarkedMemberMessage("from-the-attribute", "Orders"),
            opts => opts.MessageDeduplication
                .ByMessage<MarkedMemberMessage>(x => $"configured|{x.ProjectionName}"));

        envelope.DeduplicationId.ShouldBe("configured|Orders");
    }

    [Fact]
    public void more_than_one_marked_member_is_a_configuration_error()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            DeduplicationIdentity.DetermineIdentityMember(typeof(TwoMarkedMembersMessage)));

        ex.Message.ShouldContain("more than one member");
    }

    [Fact]
    public void naming_a_member_that_does_not_exist_is_a_configuration_error()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            DeduplicationIdentity.DetermineIdentityMember(typeof(BadMemberNameMessage)));

        ex.Message.ShouldContain("no public property or field by that name");
    }
}

public record PlainMessage(string Name);

public record NoMatchingMemberMessage(string Description);

public record MarkedMemberMessage([property: DeduplicationIdentity] string OccurrenceKey, string ProjectionName);

[DeduplicationIdentity(nameof(OccurrenceKey))]
public record NamedMemberMessage(string ProjectionName, string OccurrenceKey);

public record GuidIdentityMessage([property: DeduplicationIdentity] Guid CommandId);

public record TwoMarkedMembersMessage(
    [property: DeduplicationIdentity] string One,
    [property: DeduplicationIdentity] string Two);

[DeduplicationIdentity("NotARealMember")]
public record BadMemberNameMessage(string Name);

public interface IHaveAKey
{
    string Key { get; }
}

public class SpecificMessage : IHaveAKey
{
    public string Key { get; set; } = string.Empty;
}

using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
/// A message that never crossed a wire is stored with no content type, so reading its dead letter row back
/// has no serializer to look up. Hydrating it used to throw ArgumentNullException out of
/// MessageStoreCollection.FetchDeadLetterEnvelopesAsync, which takes the whole page of dead letters with it
/// -- including every readable row next to it.
/// </summary>
public class dead_letter_envelope_without_a_content_type
{
    [Fact]
    public void try_find_serializer_returns_null_for_a_missing_content_type()
    {
        var options = new WolverineOptions();

        options.TryFindSerializer(null).ShouldBeNull();
        options.TryFindSerializer("").ShouldBeNull();
        options.TryFindSerializer(EnvelopeConstants.JsonContentType).ShouldNotBeNull();
    }

    [Fact]
    public async Task try_read_data_does_not_throw()
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Discovery.IncludeType(typeof(ContentTypelessMessageHandler));
            opts.PublishAllMessages().To("stub://one");
        }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // Both are needed to reach the serializer lookup at all: TryReadData returns early without a
        // Destination, and only looks for a serializer for a message type that has a handler.
        var envelope = new Envelope(new ContentTypelessMessage())
        {
            Id = Guid.NewGuid(),
            MessageType = typeof(ContentTypelessMessage).ToMessageTypeName(),
            Destination = "stub://one".ToUri(),
            ContentType = null
        };

        var deadLetter = new DeadLetterEnvelope(
            envelope.Id,
            null,
            envelope,
            envelope.MessageType!,
            "stub://one",
            "test",
            typeof(InvalidOperationException).FullName!,
            "boom",
            DateTimeOffset.UtcNow,
            false);

        Should.NotThrow(() => deadLetter.TryReadData(runtime));

        // Nothing to deserialize without a serializer, so the row still reads -- it just has no message.
        deadLetter.Message.ShouldBeNull();
    }
}

public record ContentTypelessMessage;

public static class ContentTypelessMessageHandler
{
    public static void Handle(ContentTypelessMessage message)
    {
    }
}

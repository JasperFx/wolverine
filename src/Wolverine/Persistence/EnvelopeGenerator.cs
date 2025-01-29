using System.Text.Json;
using MassTransit;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Persistence;

/// <summary>
/// Used to generate fake Envelope data for test harnesses including
/// CritterWatch
/// </summary>
public class EnvelopeGenerator
{
    public IMessageSerializer Serializer { get; set; } = new SystemTextJsonSerializer(new JsonSerializerOptions());
    
    private static readonly string[] _messages =
        ["No good", "You stink", "The system was tired", "Just don't want to do this right now"];

    public static string RandomMessage() => _messages[Random.Shared.Next(0, _messages.Length - 1)];
    
    public Uri ReceivedAt { get; set; } = TransportConstants.DurableLocalUri;
    public Func<string, Exception> ExceptionSource { get; set; } = msg => new InvalidOperationException(msg);
    public Func<object> MessageSource { get; set; } = () => throw new NotImplementedException();
    
    public DateTimeOffset StartingTime { get; set; }

    public int Count { get; set; } = 100;
    
    public string TenantId { get; set; } = "*Default*";

    public Envelope BuildEnvelope()
    {
        var envelope = new Envelope(MessageSource())
        {
            Id = NewId.NextSequentialGuid(),
            Destination = ReceivedAt,
            TenantId = TenantId,
            Serializer = Serializer,
            ContentType = Serializer.ContentType,
            Attempts = Random.Shared.Next(1, 3)
        };
        
        envelope.Data = envelope.Serializer.WriteMessage(envelope.Message);
        
        if (Random.Shared.Next(0, 10) < 2)
        {
            envelope.DeliverBy = new DateTimeOffset(DateTime.Today.AddHours(1));
        }
        
        envelope.SentAt = StartingTime;
        
        envelope.Status = EnvelopeStatus.Incoming;

        envelope.OwnerId = 0;

        StartingTime = StartingTime.AddMinutes(1);

        return envelope;
    }

    public Task WriteDeadLetters(IMessageStore store) => WriteDeadLetters(Count, store);

    public async Task WriteDeadLetters(int count, IMessageStore store)
    {
        var envelopes = new List<Envelope>();
        for (int i = 0; i < count; i++)
        {
            envelopes.Add(BuildEnvelope());
        }

        await store.Inbox.StoreIncomingAsync(envelopes);

        foreach (var envelope in envelopes)
        {
            var ex = ExceptionSource(RandomMessage());
            await store.Inbox.MoveToDeadLetterStorageAsync(envelope, ex);
        }
    }

}
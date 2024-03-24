using Wolverine.Persistence.Durability;

namespace DocumentationSamples;

public class DeadLetterQueue
{
    #region sample_FetchErrorReport

    public async Task load_error_report(IMessageStore messageStore, Guid envelopeId)
    {
        var deadLetterEnvelope = await messageStore.DeadLetters.DeadLetterEnvelopeByIdAsync(envelopeId);

        // The Id
        Console.WriteLine(deadLetterEnvelope!.Envelope.Id);

        // The underlying message typ
        Console.WriteLine(deadLetterEnvelope.Envelope.MessageType);

        // The name of the system that sent the message
        Console.WriteLine(deadLetterEnvelope.Envelope.Source);

        // The .Net Exception type name
        Console.WriteLine(deadLetterEnvelope.ExceptionType);

        // Just the message of the exception
        Console.WriteLine(deadLetterEnvelope.ExceptionMessage);
    }

    #endregion
}
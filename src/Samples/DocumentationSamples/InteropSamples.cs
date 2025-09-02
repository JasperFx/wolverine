using Wolverine;

namespace DocumentationSamples;

public class InteropSamples
{
    public static void build_envelope()
    {
        #region sample_create_an_outgoing_envelope

        var message = new ApproveInvoice("1234");
        
        // I'm really creating an outgoing message here
        var envelope = new Envelope(message);
        
        // This information is assigned internally,
        // but it's good to know that it exists
        envelope.CorrelationId = "AAA";
        
        // This would refer to whatever Wolverine message
        // started a set of related activity
        envelope.ConversationId = Guid.NewGuid();

        // For both outgoing and incoming messages,
        // this identifies how the message data is structured
        envelope.ContentType = "application/json";

        // When using multi-tenancy, this is used to track
        // what tenant a message applies to
        envelope.TenantId = "222";

        // Not every broker cares about this of course
        envelope.GroupId = "BBB";

        #endregion
    }
    
    
}

public record ApproveInvoice(string Id);
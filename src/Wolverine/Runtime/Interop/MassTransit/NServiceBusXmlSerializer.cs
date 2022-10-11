using System;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Runtime.Interop.MassTransit;

public class NServiceBusXmlSerializer : IMessageSerializer
{
    
    
    public string ContentType { get; } = "text/xml";
    public byte[] Write(Envelope envelope)
    {
        throw new NotImplementedException();
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        throw new NotImplementedException();
    }

    public object ReadFromData(byte[] data)
    {
        throw new NotImplementedException();
    }

    public byte[] WriteMessage(object message)
    {
        throw new NotImplementedException();
    }
}
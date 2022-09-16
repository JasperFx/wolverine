using System;
using Wolverine;
using Wolverine.Attributes;
using TestMessages;

namespace TestingSupport.Compliance
{
    public class MessageConsumer
    {


        public void Consume(Envelope envelope, Message1 message)
        {
        }

        [RequeueOn(typeof(DivideByZeroException))]
        public void Consume(Envelope envelope, Message2 message)
        {
            if (envelope.Attempts < 2) throw new DivideByZeroException();

        }

        public void Consume(Envelope envelope, TimeoutsMessage message)
        {
            if (envelope.Attempts < 2) throw new TimeoutException();

        }
    }
}

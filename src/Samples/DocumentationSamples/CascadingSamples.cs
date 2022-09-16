using Wolverine;

namespace DocumentationSamples
{
    public class MyMessage
    {

    }

    public class MyResponse
    {

    }

    #region sample_NoCascadingHandler
    public class NoCascadingHandler
    {
        private readonly IMessageContext _bus;

        public NoCascadingHandler(IMessageContext bus)
        {
            _bus = bus;
        }

        public void Consume(MyMessage message)
        {
            // do whatever work you need to for MyMessage,
            // then send out a new MyResponse
            _bus.SendAsync(new MyResponse());
        }
    }
    #endregion


    #region sample_CascadingHandler
    public class CascadingHandler
    {
        public MyResponse Consume(MyMessage message)
        {
            return new MyResponse();
        }
    }
    #endregion


    #region sample_Request/Replay_with_cascading
    public class Requester
    {
        private readonly IMessageContext _bus;

        public Requester(IMessageContext bus)
        {
            _bus = bus;
        }

        public ValueTask GatherResponse()
        {
            return _bus.SendAsync(new MyMessage(), DeliveryOptions.RequireResponse<MyResponse>());
        }
    }
    #endregion


    public class DirectionRequest
    {
        public string Direction { get; set; }
    }

    public class GoNorth{}
    public class GoSouth {}

    #region sample_ConditionalResponseHandler
    public class ConditionalResponseHandler
    {
        public object Consume(DirectionRequest request)
        {
            switch (request.Direction)
            {
                case "North":
                    return new GoNorth();
                case "South":
                    return new GoSouth();
            }

            // This does nothing
            return null;
        }
    }
    #endregion

    public class GoWest{}
    public class GoEast{}

    #region sample_DelayedResponseHandler
    public class ScheduledResponseHandler
    {
        public Envelope Consume(DirectionRequest request)
        {
            return new Envelope(new GoWest()).ScheduleDelayed(TimeSpan.FromMinutes(5));
        }

        public Envelope Consume(MyMessage message)
        {
            // Process GoEast at 8 PM local time
            return new Envelope(new GoEast()).ScheduleAt(DateTime.Today.AddHours(20));
        }
    }
    #endregion


    #region sample_MultipleResponseHandler
    public class MultipleResponseHandler
    {
        public IEnumerable<object> Consume(MyMessage message)
        {
            // Go North now
            yield return new GoNorth();

            // Go West in an hour
            yield return new Envelope(new GoWest()).ScheduleDelayed(TimeSpan.FromHours(1));
        }
    }
    #endregion

    #region sample_TupleResponseHandler
    public class TupleResponseHandler
    {
        // Both GoNorth and GoWest will be interpreted as
        // cascading messages
        public (GoNorth, GoWest) Consume(MyMessage message)
        {
            return (new GoNorth(), new GoWest());
        }
    }
    #endregion
}

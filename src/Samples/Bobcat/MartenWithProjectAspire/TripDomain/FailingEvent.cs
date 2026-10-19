namespace TripDomain;

public class FailingEvent
{
    public static bool SerializationFails = false;

    public FailingEvent()
    {
        if (SerializationFails) throw new DivideByZeroException("Boom!");
    }
}
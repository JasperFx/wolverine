namespace LoadTesting.Trips;

public class TransientException : Exception
{
    public TransientException(string? message) : base(message)
    {
    }
}

public class OtherTransientException : Exception
{
    public OtherTransientException(string? message) : base(message)
    {
    }
}

public class RepairShopTooBusyException : Exception
{
    public RepairShopTooBusyException(string? message) : base(message)
    {
    }
}

public class TripServiceTooBusyException : Exception
{
    public TripServiceTooBusyException(string? message) : base(message)
    {
    }
}

public class TrackingUnavailableException : Exception
{
    public TrackingUnavailableException(string? message) : base(message)
    {
    }
}

public class DatabaseIsTiredException : Exception
{
    public DatabaseIsTiredException(string? message) : base(message)
    {
    }
}
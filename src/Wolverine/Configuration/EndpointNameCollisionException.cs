namespace Wolverine.Configuration;

/// <summary>
/// GH-5821. Thrown at bootstrap when two configured endpoints on one transport have names that differ only in case.
/// Wolverine identifies an endpoint by its <see cref="Uri"/>, and <see cref="Uri"/> lower cases the host, so on a
/// transport whose names are case-sensitive such a pair would share one identity and be conflated at runtime.
/// </summary>
public class EndpointNameCollisionException : Exception
{
    public EndpointNameCollisionException(string message) : base(message)
    {
    }
}

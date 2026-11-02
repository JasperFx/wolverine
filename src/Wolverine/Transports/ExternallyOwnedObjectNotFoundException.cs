namespace Wolverine.Transports;

/// <summary>
///     GH-5342. Thrown when a broker object marked as externally owned -- a queue, topic, stream or consumer this
///     application uses but does not own -- does not exist. Wolverine will not create an object it does not own, so
///     whoever owns it has to create it first. Every transport throws this same exception, so callers can catch it
///     without knowing which broker they are on.
/// </summary>
public class ExternallyOwnedObjectNotFoundException : InvalidOperationException
{
    public ExternallyOwnedObjectNotFoundException(string objectName, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ObjectName = objectName;
    }

    /// <summary>
    ///     The name of the missing broker object, as the broker knows it
    /// </summary>
    public string ObjectName { get; }
}

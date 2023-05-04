
namespace Wolverine;

public class ErrorReport
{
    public ErrorReport(Envelope envelope)
    {
        Envelope = envelope;
    }

    public ErrorReport(Envelope envelope, Exception ex)
    {
        ExceptionMessage = ex.Message;
        ExceptionType = ex.GetType().FullName;
        Exception = ex;
        Envelope = envelope;
    }

    public Envelope Envelope { get; }

    public Exception? Exception { get; set; }

    public Guid Id => Envelope.Id;

    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
}
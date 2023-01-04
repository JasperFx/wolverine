using System;

namespace Wolverine;

public class ErrorReport
{
    public const string ExceptionDetected = "Exception Detected";

    public ErrorReport(Envelope envelope)
    {
        Envelope = envelope;
    }

    public ErrorReport(Envelope envelope, Exception ex)
    {
        ExceptionMessage = ex.Message;
        ExceptionType = ex.GetType().FullName;

        Envelope = envelope;
    }

    public Envelope Envelope { get; }


    public Guid Id => Envelope.Id;

    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
}
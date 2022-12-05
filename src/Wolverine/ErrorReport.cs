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
        ExceptionText = ex.ToString();
        ExceptionMessage = ex.Message;
        ExceptionType = ex.GetType().FullName;
        Explanation = ExceptionDetected;

        Envelope = envelope;
    }

    public Envelope Envelope { get; }


    public Guid Id => Envelope.Id;

    public string? Explanation { get; set; }

    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public string? ExceptionText { get; set; }
}
using System;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Runtime;

public class ErrorReportTests
{
    private readonly Envelope theEnvelope;
    private readonly ErrorReport theErrorReport;
    private readonly TimeoutException theException;

    public ErrorReportTests()
    {
        theEnvelope = new Envelope();
        theEnvelope.ContentType = EnvelopeConstants.JsonContentType;
        theEnvelope.Data = new byte[] { 1, 2, 3, 4 };
        theEnvelope.Source = "OtherApp";
        theEnvelope.Destination = TransportConstants.RepliesUri;

        theException = new TimeoutException("Boo!");

        theErrorReport = new ErrorReport(theEnvelope, theException);
    }

    [Fact]
    public void captures_exception_data()
    {
        theErrorReport.ExceptionMessage.ShouldBe(theException.Message);
        theErrorReport.ExceptionType.ShouldBe(theException.GetType().FullName);
    }

    [Fact]
    public void copy_the_id()
    {
        theErrorReport.Id.ShouldBe(theEnvelope.Id);
    }
}
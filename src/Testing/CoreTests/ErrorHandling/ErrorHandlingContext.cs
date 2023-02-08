using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TestingSupport;
using TestingSupport.ErrorHandling;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Tracking;

namespace CoreTests.ErrorHandling;

public class ErrorHandlingContext
{
    protected readonly ErrorCausingMessage theMessage = new();

    protected readonly WolverineOptions theOptions = new();
    private ITrackedSession _session;

    public ErrorHandlingContext()
    {
        theOptions.DisableConventionalDiscovery()
            .Policies.Discovery(x => x.IncludeType<ErrorCausingMessageHandler>());
    }

    protected void throwOnAttempt<T>(int attempt) where T : Exception, new()
    {
        theMessage.Errors.Add(attempt, new T());
    }

    protected async Task<EnvelopeRecord> afterProcessingIsComplete()
    {
        using var host = WolverineHost.For(theOptions);
        _session = await host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(theMessage);

        return _session.AllRecordsInOrder().Where(x => !(x.Envelope.Message is FailureAcknowledgement)).LastOrDefault(
            x =>
                x.EventType == EventType.MessageSucceeded || x.EventType == EventType.MovedToErrorQueue);
    }

    protected async Task shouldSucceedOnAttempt(int attempt)
    {
        using var host = WolverineHost.For(theOptions);
        var session = await host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(theMessage);

        var record = session.AllRecordsInOrder().Where(x => !(x.Envelope.Message is FailureAcknowledgement))
            .LastOrDefault(x =>
                x.EventType == EventType.MessageSucceeded || x.EventType == EventType.MovedToErrorQueue);

        if (record == null)
        {
            throw new Exception("No ending activity detected");
        }

        if (record.EventType == EventType.MessageSucceeded && record.AttemptNumber == attempt)
        {
            return;
        }

        var writer = new StringWriter();

        writer.WriteLine($"Actual ending was '{record.EventType}' on attempt {record.AttemptNumber}");
        foreach (var envelopeRecord in session.AllRecordsInOrder())
        {
            writer.WriteLine(envelopeRecord);
            if (envelopeRecord.Exception != null)
            {
                writer.WriteLine(envelopeRecord.Exception.Message);
            }
        }

        throw new Exception(writer.ToString());
    }

    protected async Task shouldMoveToErrorQueueOnAttempt(int attempt)
    {
        using var host = WolverineHost.For(theOptions);
        var session = await host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(theMessage);

        var record = session.AllRecordsInOrder().Where(x => !(x.Envelope.Message is FailureAcknowledgement))
            .LastOrDefault(x =>
                x.EventType == EventType.MessageSucceeded || x.EventType == EventType.MovedToErrorQueue);

        if (record == null)
        {
            throw new Exception("No ending activity detected");
        }

        if (record.EventType == EventType.MovedToErrorQueue && record.AttemptNumber == attempt)
        {
            return;
        }

        var writer = new StringWriter();

        writer.WriteLine($"Actual ending was '{record.EventType}' on attempt {record.AttemptNumber}");
        foreach (var envelopeRecord in session.AllRecordsInOrder())
        {
            writer.WriteLine(envelopeRecord);
            if (envelopeRecord.Exception != null)
            {
                writer.WriteLine(envelopeRecord.Exception.Message);
            }
        }

        throw new Exception(writer.ToString());
    }
}
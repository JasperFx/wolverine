using Microsoft.Extensions.Hosting;
using TestingSupport;
using TestingSupport.ErrorHandling;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Tracking;

namespace CoreTests.ErrorHandling;

public class ErrorHandlingContext : IDisposable
{
    protected readonly ErrorCausingMessage theMessage = new();

    private ITrackedSession _session;
    private IHost _host;

    public ErrorHandlingContext()
    {
    }

    protected void ConfigureOptions(Action<WolverineOptions> configure)
    {
        _host = Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            configure(opts);
            opts.DisableConventionalDiscovery().IncludeType<ErrorCausingMessageHandler>();
        }).Start();
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    protected void throwOnAttempt<T>(int attempt) where T : Exception, new()
    {
        theMessage.Errors.Add(attempt, new T());
    }

    protected async Task<EnvelopeRecord> afterProcessingIsComplete()
    {
        if (_host == null)
        {
            ConfigureOptions(_ => {});
        }
        
        _session = await _host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(theMessage);

        return _session.AllRecordsInOrder().Where(x => !(x.Envelope.Message is FailureAcknowledgement)).LastOrDefault(
            x =>
                x.MessageEventType == MessageEventType.MessageSucceeded || x.MessageEventType == MessageEventType.MovedToErrorQueue);
    }

    protected async Task shouldSucceedOnAttempt(int attempt)
    {
        if (_host == null)
        {
            ConfigureOptions(_ => {});
        }
        
        var session = await _host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(theMessage);

        var record = session.AllRecordsInOrder().Where(x => !(x.Envelope.Message is FailureAcknowledgement))
            .LastOrDefault(x =>
                x.MessageEventType == MessageEventType.MessageSucceeded || x.MessageEventType == MessageEventType.MovedToErrorQueue);

        if (record == null)
        {
            throw new Exception("No ending activity detected");
        }

        if (record.MessageEventType == MessageEventType.MessageSucceeded && record.AttemptNumber == attempt)
        {
            return;
        }

        var writer = new StringWriter();

        writer.WriteLine($"Actual ending was '{record.MessageEventType}' on attempt {record.AttemptNumber}");
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
        if (_host == null)
        {
            ConfigureOptions(_ => {});
        }
        
        var session = await _host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(theMessage);

        var record = session.AllRecordsInOrder().Where(x => !(x.Envelope.Message is FailureAcknowledgement))
            .LastOrDefault(x =>
                x.MessageEventType == MessageEventType.MessageSucceeded || x.MessageEventType == MessageEventType.MovedToErrorQueue);

        if (record == null)
        {
            throw new Exception("No ending activity detected");
        }

        if (record.MessageEventType == MessageEventType.MovedToErrorQueue && record.AttemptNumber == attempt)
        {
            return;
        }

        var writer = new StringWriter();

        writer.WriteLine($"Actual ending was '{record.MessageEventType}' on attempt {record.AttemptNumber}");
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
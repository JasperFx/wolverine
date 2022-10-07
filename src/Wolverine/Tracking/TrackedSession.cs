using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Baseline.Dates;
using Lamar;
using LamarCodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;

namespace Wolverine.Tracking;

internal class TrackedSession : ITrackedSession
{
    private readonly IList<ITrackedCondition> _conditions = new List<ITrackedCondition>();

    private readonly Cache<Guid, EnvelopeHistory> _envelopes = new(id => new EnvelopeHistory(id));

    private readonly IList<Exception> _exceptions = new List<Exception>();

    private readonly IList<WolverineRuntime> _otherHosts = new List<WolverineRuntime>();
    private readonly IHost _primaryHost;
    private readonly WolverineRuntime _primaryLogger;

    private readonly TaskCompletionSource<TrackingStatus> _source;

    private readonly Stopwatch _stopwatch = new();

    private TrackingStatus _status = TrackingStatus.Active;

    public TrackedSession(IHost host)
    {
        _primaryHost = host ?? throw new ArgumentNullException(nameof(host));
        _source = new TaskCompletionSource<TrackingStatus>();
        _primaryLogger = host.GetRuntime();
    }

    public TimeSpan Timeout { get; set; } = 5.Seconds();

    public bool AssertNoExceptions { get; set; } = true;

    // TODO -- do something with this?
    public bool AssertNoTimeout { get; set; } = true;

    public Func<IMessageContext, Task> Execution { get; set; } = _ => Task.CompletedTask;

    public bool AlwaysTrackExternalTransports { get; set; }


    public TrackingStatus Status
    {
        get => _status;
        private set
        {
            _status = value;
            _source.TrySetResult(value);

            if (value == TrackingStatus.Completed)
            {
                _stopwatch.Stop();
            }
        }
    }

    public T FindSingleTrackedMessageOfType<T>()
    {
        var messages = AllRecordsInOrder()
            .Select(x => x.Envelope.Message).Where(x => x != null)
            .OfType<T>()
            .Distinct().ToArray();

        if (messages.Length == 0)
        {
            throw new InvalidOperationException(
                $"No message of type {typeof(T).FullNameInCode()} was detected");
        }

        if (messages.Length > 1)
        {
            throw new InvalidOperationException(
                $"Expected one message of type {typeof(T).FullNameInCode()}, but detected {messages.Length}: {messages.Select(x => x!.ToString())!.Join(", ")}");
        }

        return messages
            .Single();
    }

    public T FindSingleTrackedMessageOfType<T>(EventType eventType)
    {
        var messages = AllRecordsInOrder()
            .Where(x => x.EventType == eventType)
            .Select(x => x.Envelope.Message)
            .Where(x => x != null)
            .OfType<T>()
            .Distinct().ToArray();

        return messages.Length switch
        {
            0 => throw new InvalidOperationException($"No message of type {typeof(T).FullNameInCode()} was detected"),
            > 1 => throw new InvalidOperationException(
                $"Expected one message of type {typeof(T).FullNameInCode()}, but detected {messages.Length}: {messages.Select(x => x!.ToString())!.Join(", ")}"),
            _ => messages.Single()
        };
    }

    public EnvelopeRecord[] FindEnvelopesWithMessageType<T>(EventType eventType)
    {
        return _envelopes
            .SelectMany(x => x.Records)
            .Where(x => x.EventType == eventType)
            .Where(x => x.Envelope.Message is T)
            .ToArray();
    }

    public EnvelopeRecord[] FindEnvelopesWithMessageType<T>()
    {
        return _envelopes
            .SelectMany(x => x.Records)
            .Where(x => x.Envelope.Message is T)
            .ToArray();
    }

    public EnvelopeRecord[] AllRecordsInOrder()
    {
        return _envelopes.SelectMany(x => x.Records).OrderBy(x => x.SessionTime).ToArray();
    }
    
    public EnvelopeRecord[] AllRecordsInOrder(EventType eventType)
    {
        return _envelopes
            .SelectMany(x => x.Records)
            .Where(x => x.EventType == eventType)
            .OrderBy(x => x.SessionTime)
            .ToArray();
    }

    public IReadOnlyList<Exception> AllExceptions()
    {
        return _envelopes.SelectMany(x => x.Records)
            .Select(x => x.Exception).Where(x => x != null)
            .Distinct().ToList()!;
    }

    public void WatchOther(IHost host)
    {
        if (ReferenceEquals(host, _primaryHost))
        {
            return;
        }

        _otherHosts.Add(host.GetRuntime());
    }

    public void AssertNoExceptionsWereThrown()
    {
        if (_exceptions.ToArray().Any())
        {
            throw new AggregateException(_exceptions);
        }
    }

    public void AssertNotTimedOut()
    {
        if (IsCompleted())
        {
            return;
        }

        if (Status == TrackingStatus.TimedOut)
        {
            var message =
                BuildActivityMessage($"This {nameof(TrackedSession)} timed out before all activity completed.");

            throw new TimeoutException(message);
        }
    }

    internal string BuildActivityMessage(string description)
    {
        var writer = new StringWriter();
        writer.WriteLine(description);
        writer.WriteLine("Activity detected:");
        foreach (var record in AllRecordsInOrder()) writer.WriteLine(record);

        if (_conditions.Any())
        {
            writer.WriteLine();
            writer.WriteLine("Conditions:");
            foreach (var condition in _conditions) writer.WriteLine($"{condition} ({condition.IsCompleted()})");
        }

        return writer.ToString();
    }

    private void setActiveSession(TrackedSession? session)
    {
        _primaryLogger.ActiveSession = session;
        foreach (var runtime in _otherHosts) runtime.ActiveSession = session;
    }

    public async Task ExecuteAndTrackAsync()
    {
        setActiveSession(this);

        _stopwatch.Start();

        try
        {
            await using var scope = _primaryHost.Services.As<IContainer>().GetNestedContainer();
            var context = scope.GetInstance<IMessageContext>();
            await Execution(context).WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            cleanUp();
            
            var message =
                BuildActivityMessage($"This {nameof(TrackedSession)} timed out before all activity completed.");

            throw new TimeoutException(message);
        }
        catch (Exception)
        {
            cleanUp();
            throw;
        }

        startTimeoutTracking();

        await _source.Task;

        cleanUp();

        if (AssertNoExceptions)
        {
            AssertNoExceptionsWereThrown();
        }

        if (AssertNoExceptions)
        {
            AssertNotTimedOut();
        }
    }

    public Task TrackAsync()
    {
        _stopwatch.Start();


        startTimeoutTracking();

        return _source.Task;
    }

    private void cleanUp()
    {
        setActiveSession(null);

        _stopwatch.Stop();
    }

    private void startTimeoutTracking()
    {
#pragma warning disable 4014
#pragma warning disable VSTHRD110
        Task.Factory.StartNew(async () =>
#pragma warning restore VSTHRD110
#pragma warning restore 4014
        {
            await Task.Delay(Timeout);

            Status = TrackingStatus.TimedOut;
        }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
    }

    public void Record(EventType eventType, Envelope envelope, string? serviceName, int uniqueNodeId,
        Exception? ex = null)
    {
        if (envelope.Message is ValueTask) throw new Exception("What you doing Willis?");
        var history = _envelopes[envelope.Id];

        var record = new EnvelopeRecord(eventType, envelope, _stopwatch.ElapsedMilliseconds, ex)
        {
            ServiceName = serviceName,
            UniqueNodeId = uniqueNodeId
        };

        if (AlwaysTrackExternalTransports || _otherHosts.Any())
        {
            history.RecordCrossApplication(record);
        }
        else
        {
            history.RecordLocally(record);
        }

        foreach (var condition in _conditions) condition.Record(record);

        if (ex != null)
        {
            _exceptions.Add(ex);
        }

        if (IsCompleted())
        {
            Status = TrackingStatus.Completed;
        }
    }

    public bool IsCompleted()
    {
        if (!_envelopes.All(x => x.IsComplete()))
        {
            return false;
        }

        return !_conditions.Any() || _conditions.All(x => x.IsCompleted());
    }

    public void LogException(Exception exception, string? serviceName)
    {
        Debug.WriteLine($"Exception Occurred in {serviceName}: {exception}");
        _exceptions.Add(exception);
    }

    public void AddCondition(ITrackedCondition condition)
    {
        Debug.WriteLine($"Condition Added: {condition}");
        _conditions.Add(condition);
    }

    public override string ToString()
    {
        var conditions = $"Conditions:\n{_conditions.Select(x => x.ToString())!.Join("\n")}";
        var activity = $"Activity:\n{AllRecordsInOrder().Select(x => x.ToString()).Join("\n")}";
        var exceptions = $"Exceptions:\n{_exceptions.Select(x => x.ToString()).Join("\n")}";

        return $"{conditions}\n\n{activity}\\{exceptions}";
    }

    public RecordCollection Received => new RecordCollection(EventType.Received, this);
    public RecordCollection Sent => new RecordCollection(EventType.Sent, this);
    

    public RecordCollection Executed => new RecordCollection(EventType.ExecutionFinished, this);
}

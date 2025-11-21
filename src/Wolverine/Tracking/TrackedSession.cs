using System.Diagnostics;
using JasperFx.CommandLine.TextualDisplays;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine.Tracking;

internal partial class TrackedSession : ITrackedSession
{
    private readonly IList<ITrackedCondition> _conditions = new List<ITrackedCondition>();

    private Cache<Guid, EnvelopeHistory> _envelopes = new(id => new EnvelopeHistory(id));

    private readonly IList<Exception> _exceptions = new List<Exception>();

    private readonly IList<WolverineRuntime> _otherHosts = new List<WolverineRuntime>();
    private readonly IHost _primaryHost;
    private readonly WolverineRuntime _primaryLogger;

    private readonly TaskCompletionSource<TrackingStatus> _source;

    private bool _executionComplete;

    private Stopwatch _stopwatch = new();

    private readonly List<Func<Type, bool>> _ignoreMessageRules = [t => t.CanBeCastTo<IAgentCommand>()];
    private CancellationTokenSource _cancellation = new();

    private TrackingStatus _status = TrackingStatus.Active;

    public TrackedSession(IHost host)
    {
        _primaryHost = host ?? throw new ArgumentNullException(nameof(host));
        _source = new TaskCompletionSource<TrackingStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _primaryLogger = host.GetRuntime();
    }

    public TrackedSession(IServiceProvider services) : this(new HostWrapper(services))
    {
        
    }

    // Actions to carry out first before execute and track
    public List<Func<IWolverineRuntime, CancellationToken, Task>> Befores { get; } = new();

    // All previous TrackedSessions
    public List<TrackedSession> Previous { get; } = new();
    public Queue<ISecondStateExecution> SecondaryStages { get; } = new();

    public TimeSpan Timeout { get; set; } = 5.Seconds();

    public bool AssertNoExceptions { get; set; } = true;

    public bool AssertAnyFailureAcknowledgements { get; set; } = true;

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

    public T FindSingleTrackedMessageOfType<T>(MessageEventType eventType)
    {
        var messages = AllRecordsInOrder()
            .Where(x => x.MessageEventType == eventType)
            .Select(x => x.Envelope.Message)
            .Where(x => x != null)
            .OfType<T>()
            .Distinct().ToArray();


        return messages.Length switch
        {
            0 => throw new InvalidOperationException(
                BuildActivityMessage($"No message of type {typeof(T).FullNameInCode()} was detected")),
            > 1 => throw new InvalidOperationException(
                BuildActivityMessage(
                    $"Expected one message of type {typeof(T).FullNameInCode()}, but detected {messages.Length}: {messages.Select(x => x!.ToString())!.Join(", ")}")),
            _ => messages.Single()
        };
    }

    public EnvelopeRecord[] FindEnvelopesWithMessageType<T>(MessageEventType eventType)
    {
        return _envelopes
            .SelectMany(x => x.Records)
            .Where(x => x.MessageEventType == eventType)
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

    public EnvelopeRecord[] AllRecordsInOrder(MessageEventType eventType)
    {
        return _envelopes
            .SelectMany(x => x.Records)
            .Where(x => x.MessageEventType == eventType)
            .OrderBy(x => x.SessionTime)
            .ToArray();
    }

    public IReadOnlyList<Exception> AllExceptions()
    {
        return _envelopes.SelectMany(x => x.Records)
            .Select(x => x.Exception).Where(x => x != null).Concat(_exceptions)
            .Distinct().ToList()!;
    }

    public void AssertCondition(string message, Func<bool> condition)
    {
        if (condition()) return;

        var description = BuildActivityMessage(message);
        throw new Exception(description);
    }

    public Task<ITrackedSession> PlayScheduledMessagesAsync(TimeSpan timeout)
    {
        var serviceName = _primaryHost.GetRuntime().Options.ServiceName;
        var recordsInOrder = _envelopes.SelectMany(x => x.Records).Where(x => x.MessageEventType == MessageEventType.Scheduled || x.Envelope.Status == EnvelopeStatus.Scheduled || x.WasScheduled).ToArray();
        var records = recordsInOrder.Where(x => x.ServiceName == serviceName).ToArray();
        if (!records.Any())
        {
            var message = BuildActivityMessage("No scheduled messages recorded.");
            throw new Exception(message);
        }

        var trackedSessionConfiguration = _primaryHost.TrackActivity().Timeout(timeout).As<TrackedSessionConfiguration>();
        var replayed = trackedSessionConfiguration.Session;
        replayed.AlwaysTrackExternalTransports = AlwaysTrackExternalTransports;
        replayed.AssertAnyFailureAcknowledgements = AssertAnyFailureAcknowledgements;
        replayed.AssertNoExceptions = AssertNoExceptions;
        replayed._otherHosts.AddRange(_otherHosts);
        
        return trackedSessionConfiguration.ExecuteAndWaitAsync(c => ReplayAll(c, records));
    }

    internal async Task ReplayAll(IMessageContext context, EnvelopeRecord[] records)
    {
        var envelopes = records.Select(x => x.Envelope).Distinct().ToArray();
        
        foreach (var envelope in envelopes)
        {
            if (envelope.Destination.Scheme == TransportConstants.Local)
            {
                await context.InvokeAsync(envelope.Message);
            }
            else
            {
                await context.EndpointFor(envelope.Destination).SendAsync(envelope.Message);
            }
        }
    }

    public RecordCollection Scheduled => new ScheduledActivityRecordCollection(MessageEventType.Scheduled, this);

    public RecordCollection Received => new(MessageEventType.Received, this);
    public RecordCollection Sent => new(MessageEventType.Sent, this);
    public RecordCollection ExecutionStarted => new(MessageEventType.ExecutionStarted, this);
    public RecordCollection ExecutionFinished => new(MessageEventType.ExecutionFinished, this);
    public RecordCollection MessageSucceeded => new(MessageEventType.MessageSucceeded, this);
    public RecordCollection MessageFailed => new(MessageEventType.MessageFailed, this);
    public RecordCollection NoHandlers => new(MessageEventType.NoHandlers, this);
    public RecordCollection NoRoutes => new(MessageEventType.NoRoutes, this);
    public RecordCollection MovedToErrorQueue => new(MessageEventType.MovedToErrorQueue, this);
    public RecordCollection Requeued => new(MessageEventType.Requeued, this);
    public RecordCollection Executed => new(MessageEventType.ExecutionFinished, this);

    public RecordCollection Discarded => new(MessageEventType.Discarded, this);

    public void WatchOther(IHost host)
    {
        if (ReferenceEquals(host, _primaryHost))
        {
            return;
        }

        _otherHosts.Add(host.GetRuntime());
    }

    public void WatchOther(IServiceProvider services)
    {
        if (ReferenceEquals(services, _primaryHost.Services))
        {
            return;
        }
        
        _otherHosts.Add((WolverineRuntime)services.GetRequiredService<IWolverineRuntime>());
    }
    
    public void AssertNoExceptionsWereThrown()
    {
        if (_exceptions.Count > 0)
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

        var grid = new Grid<EnvelopeRecord>();

        var records = AllRecordsInOrder();

        if (records.Length == 0)
        {
            writer.WriteLine("No activity detected!");
        }
        else
        {
            writeGrid(grid, records, writer);
        }

        if (_conditions.Any())
        {
            writer.WriteLine();
            writer.WriteLine("Conditions:");
            foreach (var condition in _conditions) writer.WriteLine($"{condition} ({condition.IsCompleted()})");
        }

        if (_exceptions.Any())
        {
            writer.WriteLine();
            writer.WriteLine("Exceptions detected: ");
            foreach (var exception in _exceptions)
            {
                writer.WriteLine(exception.ToString());
                writer.WriteLine();
            }
        }

        return writer.ToString();
    }

    private void writeGrid(Grid<EnvelopeRecord> grid, EnvelopeRecord[] records, StringWriter writer)
    {
        if (_otherHosts.Any())
        {
            grid.AddColumn("Service (Node Id)", x => $"{x.ServiceName} ({x.UniqueNodeId})");
        }

        grid.AddColumn("Message Id", x => x.Envelope.Id.ToString());
        grid.AddColumn("Message Type", x => x.Envelope.MessageType ?? string.Empty);
        grid.AddColumn("Time (ms)", x => x.SessionTime.ToString(), true);

        grid.AddColumn("Event", x => x.MessageEventType.ToString());

        var text = grid.Write(records);
        writer.WriteLine(text);
    }

    private void setActiveSession(TrackedSession? session)
    {
        _primaryLogger.ActiveSession = session;
        foreach (var runtime in _otherHosts) runtime.ActiveSession = session;
    }
    
    public void AssertNoFailureAcksWereSent()
    {
        var records = AllRecordsInOrder().Where(x => x.Message is FailureAcknowledgement).ToArray();
        if (records.Any())
        {
            var writer = new StringWriter();
            writer.WriteLine($"{nameof(FailureAcknowledgement)} messages were detected. ");
            writer.WriteLine($"Configure the tracked activity with {nameof(TrackedSessionConfiguration.IgnoreFailureAcks)}() to ignore these failure acks in the test.");
            foreach (EnvelopeRecord record in records)
            {
                writer.WriteLine(record.Message.As<FailureAcknowledgement>().Message);
            }

            throw new Exception(writer.ToString());
        }
        
    }

    public Task TrackAsync()
    {
        return ExecuteAndTrackAsync();
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

            await _cancellation.CancelAsync();
        }, CancellationToken.None, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);
    }
    
    public void MaybeRecord(MessageEventType messageEventType, Envelope envelope, string serviceName, Guid uniqueNodeId)
    {
        if (envelope.Message is ValueTask)
        {
            throw new Exception("Whatcha you doing Willis?");
        }

        // Ignore these
        var messageType = envelope.Message?.GetType();
        if (_ignoreMessageRules.Any(x => x(messageType)))
        {
            return;
        }
        
        // Really just doing this idempotently
        var history = _envelopes[envelope.Id];
        if (history.Records.Any(r =>
                r.MessageEventType == messageEventType && object.ReferenceEquals(r.Envelope, envelope)))
        {
            return;
        }
        
        Record(messageEventType, envelope, serviceName, uniqueNodeId);
    }

    public void Record(MessageEventType eventType, Envelope envelope, string? serviceName, Guid uniqueNodeId,
        Exception? ex = null)
    {
        // Ignore these
        var messageType = envelope.Message?.GetType();
        if (messageType != null && _ignoreMessageRules.Any(x => x(messageType)))
        {
            return;
        }

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
        if (!_executionComplete) return false;

        if (_conditions.Any(x => x.IsCompleted()))
        {
            return true;
        }

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

    public void IgnoreMessageTypes(Func<Type, bool> filter)
    {
        _ignoreMessageRules.Add(filter);
    }
}

internal class HostWrapper : IHost
{
    public HostWrapper(IServiceProvider services)
    {
        Services = services;
    }

    public void Dispose()
    {
        if (Services is IDisposable disposable) disposable.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        return Task.CompletedTask;
    }

    public IServiceProvider Services { get; }
}
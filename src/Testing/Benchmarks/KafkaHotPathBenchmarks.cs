using System.Reflection;
using System.Text;
using BenchmarkDotNet.Attributes;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Kafka;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Serialization;
using Wolverine.Util;

namespace Benchmarks;

/// <summary>
/// Quantifies Wolverine's per-message fixed costs on the Kafka receive/send hot path
/// per KAFKA-PERF-DEEP-DIVE-PLAN.md §2/H2 (theory T6). Each benchmark isolates one
/// warm-path cost the listener/sender pays for every single message.
/// </summary>
[MemoryDiagnoser]
public class KafkaHotPathBenchmarks
{
    private const string TopicName = "hot-path-benchmarks";

    private IHost _host = null!;
    private KafkaTransport _transport = null!;
    private KafkaTopic _topic = null!;
    private IKafkaEnvelopeMapper _mapper = null!;
    private HandlerGraph _handlers = null!;
    private Func<string?, IMessageSerializer?> _tryFindSerializer = null!;

    private Envelope _outgoing = null!;
    private Envelope _restampTarget = null!;
    private Message<string, byte[]> _incoming = null!;
    private KafkaHotPathMessage _message = null!;
    private byte[] _payload = null!;
    private string _messageTypeName = null!;

    // Mirrors Executor's per-handler timeout (Executor.cs:227) — the CTS ctor arms a timer
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(60);
    private readonly CancellationTokenSource _appStopping = new();

    // Public so the raw-field baseline write is observable (and warning-clean)
    public object? RawMessageField;

    [GlobalSetup]
    public void Setup()
    {
        // A real (if minimal) Wolverine host: gives us a live IWolverineRuntime so the
        // Kafka endpoint compiles exactly as it would inside KafkaTransport.ConnectAsync —
        // including the Endpoint.Compile serializer pre-seed (Endpoint.cs:524-537).
        _host = new HostBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(KafkaHotPathBenchmarks).Assembly;
                opts.Discovery.IncludeAssembly(typeof(KafkaHotPathBenchmarks).Assembly);
                opts.Durability.Mode = DurabilityMode.MediatorOnly;
            })
            .Start();

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        // Standalone transport + topic, compiled against the live runtime — the same
        // Compile(runtime) call KafkaTransport.ConnectAsync makes per topic. No broker needed.
        _transport = new KafkaTransport();
        _topic = _transport.Topics[TopicName];
        _topic.Compile(runtime);
        _mapper = _topic.BuildMapper(runtime);

        _message = new KafkaHotPathMessage(Guid.NewGuid(), "hot-path", 42);
        _messageTypeName = typeof(KafkaHotPathMessage).ToMessageTypeName();
        _payload = Encoding.UTF8.GetBytes("{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"hot-path\",\"number\":42}");

        // Fully-populated outgoing envelope: drives ~14 reserved headers through the
        // outgoing mapper, matching what a real Wolverine sender stamps per message.
        _outgoing = new Envelope
        {
            Data = _payload,
            ContentType = "application/json",
            CorrelationId = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid(),
            ParentId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            SagaId = "saga-42",
            Source = "KafkaHotPathBenchmarks",
            ReplyUri = new Uri("kafka://topic/replies"),
            TenantId = "tenant-1",
            UserName = "benchmark-user",
            DeliverBy = DateTimeOffset.UtcNow.AddDays(365),
            Attempts = 1,
            PartitionKey = "partition-key-1",
            TopicName = TopicName,
            Message = _message // stamps MessageType via ToMessageTypeName
        };

        // Round-trip a real outgoing envelope through the mapper ONCE to get a wire-realistic
        // incoming Message<string, byte[]> (same header set Wolverine itself would write).
        _incoming = createMessage(_mapper, _outgoing);
        Console.WriteLine($"// Incoming benchmark message carries {_incoming.Headers.Count} headers");

        // Standalone-but-real HandlerGraph for the message-type name lookup. (The runtime's
        // own graph hangs off the internal WolverineOptions.HandlerGraph property; a fresh
        // graph + RegisterMessageType exercises the identical ImHashMap<string, Type> read.)
        _handlers = new HandlerGraph();
        _handlers.RegisterMessageType(typeof(KafkaHotPathMessage));

        // Endpoint.TryFindSerializer is internal; bind a delegate once so the benchmark
        // measures the real method body + one delegate invoke (~1ns overhead).
        var method = typeof(Endpoint).GetMethod("TryFindSerializer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        _tryFindSerializer = method.CreateDelegate<Func<string?, IMessageSerializer?>>(_topic);

        _restampTarget = new Envelope();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _host.Dispose();
        _appStopping.Dispose();
    }

    /// <summary>
    /// Headline: the full incoming map — header dictionary copy (writeIncomingHeaders) plus
    /// the 19 typed reader re-scans/re-decodes of the Kafka header list, plus the serializer
    /// resolution. Body replicates the internal KafkaTransportExtensions.CreateEnvelope
    /// (KafkaTransportExtensions.cs:250-264), which is what KafkaListener calls per record —
    /// including the "wasted" sequential-Guid Envelope id the wire id header then clobbers.
    /// </summary>
    [Benchmark]
    public Envelope MapIncoming()
    {
        var envelope = new Envelope
        {
            PartitionKey = _incoming.Key,
            Data = _incoming.Value,
            TopicName = TopicName
        };

        _mapper.MapIncomingToEnvelope(envelope, _incoming);
        return envelope;
    }

    /// <summary>
    /// The outgoing twin: per-send Message construction + MapEnvelopeToOutgoing, which pays
    /// _envelopeToHeader.Values.ToArray() (EnvelopeMapper.cs:391-397) and ~18 UTF8 header
    /// writes per message. Body replicates the internal KafkaTransportExtensions.CreateMessage
    /// (KafkaTransportExtensions.cs:266-289) minus the (synchronously-completed) GetDataAsync.
    /// </summary>
    [Benchmark]
    public Message<string, byte[]> MapOutgoing()
    {
        var message = new Message<string, byte[]>
        {
            Key = !string.IsNullOrEmpty(_outgoing.PartitionKey) ? _outgoing.PartitionKey : _outgoing.Id.ToString(),
            Value = _payload,
            Headers = new Headers()
        };

        _mapper.MapEnvelopeToOutgoing(_outgoing, message);
        return message;
    }

    /// <summary>
    /// Bare envelope allocation, including the sequential-Guid Id generated in the field
    /// initializer (Envelope.Id) and the header dictionary field.
    /// </summary>
    [Benchmark]
    public Envelope EnvelopeAlloc()
    {
        return new Envelope();
    }

    /// <summary>
    /// Warm HandlerGraph.TryFindMessageType hit — the string-keyed ImHashMap read the
    /// pipeline pays at least twice per message (once directly, once inside the
    /// RequiresEncryption check; HandlerPipeline.cs:190,286-300).
    /// </summary>
    [Benchmark]
    public Type? TypeNameLookup()
    {
        _handlers.TryFindMessageType(_messageTypeName, out var messageType);
        return messageType;
    }

    /// <summary>
    /// Warm Endpoint.TryFindSerializer("application/json") hit on a compiled endpoint —
    /// pure ImHashMap read thanks to the Compile() pre-seed (Endpoint.cs:593-613).
    /// </summary>
    [Benchmark]
    public IMessageSerializer? SerializerLookup()
    {
        return _tryFindSerializer("application/json");
    }

    /// <summary>
    /// Envelope.Message property setter: re-stamps MessageType via the reverse
    /// ToMessageTypeName lookup on every assignment (Envelope.cs:225-233).
    /// </summary>
    [Benchmark]
    public void MessageSetterRestamp()
    {
        _restampTarget.Message = _message;
    }

    /// <summary>
    /// Baseline for MessageSetterRestamp: what a raw field assignment would cost.
    /// </summary>
    [Benchmark]
    public void MessageRawFieldBaseline()
    {
        RawMessageField = _message;
    }

    /// <summary>
    /// The Executor.ExecuteAsync per-message fixed cost: a timeout CTS (arms a timer) plus a
    /// linked CTS with its registration, then disposal of both (Executor.cs:227-228).
    /// </summary>
    [Benchmark]
    public CancellationToken CtsPairPerMessage()
    {
        using var timeout = new CancellationTokenSource(ExecutionTimeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _appStopping.Token);
        return combined.Token;
    }

    // Replica of the internal KafkaTransportExtensions.CreateMessage used once in setup to
    // produce a wire-realistic incoming message by round-tripping the outgoing envelope.
    private static Message<string, byte[]> createMessage(IKafkaEnvelopeMapper mapper, Envelope envelope)
    {
        var message = new Message<string, byte[]>
        {
            Key = envelope.PartitionKey!,
            Value = envelope.Data!,
            Headers = new Headers()
        };

        mapper.MapEnvelopeToOutgoing(envelope, message);
        return message;
    }
}

public record KafkaHotPathMessage(Guid Id, string Name, int Number);

public static class KafkaHotPathMessageHandler
{
    public static void Handle(KafkaHotPathMessage message)
    {
        // no-op: exists so the benchmark host has a real discovered handler
    }
}

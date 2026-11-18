using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Wolverine.Configuration;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.Sagas;

// GH-5415: the generated code of an event-sourced saga's handler. The saga's stream is loaded for writing with the
// store's FetchForWriting (IEventSourcingFrameProvider.BuildLoadAggregateFrame), the saga is the stream's aggregate --
// the Inline snapshot Wolverine registered -- and whatever the saga's methods return is appended to that stream or sent.

/// <summary>
/// The saga lookup of an event-sourced saga: the saga is the aggregate of its stream. A completed saga keeps its
/// stream, so there are three branches: no saga, a completed saga, and a live saga. Only a live saga reaches the
/// saga's handler methods.
/// </summary>
internal class EventSourcedSagaGuardFrame : Frame
{
    private Frame[] _completedPath = [];
    private Frame[] _existsPath = [];
    private Frame[] _nullPath = [];
    private readonly Variable _stream;

    public EventSourcedSagaGuardFrame(Type sagaType, Variable stream) : base(true)
    {
        _stream = stream;
        uses.Add(stream);
        Saga = new Variable(sagaType, this);
    }

    public Variable Saga { get; }

    /// <summary>The three branches, built once the saga variable exists</summary>
    public void UsePaths(Frame[] nullPath, Frame[] completedPath, Frame[] existsPath)
    {
        _nullPath = nullPath;
        _completedPath = completedPath;
        _existsPath = existsPath;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("The event-sourced saga is the aggregate of its own stream");
        writer.Write($"var {Saga.Usage} = {_stream.Usage}.{nameof(IEventStream<string>.Aggregate)};");

        writer.Write($"BLOCK:if ({Saga.Usage} == null)");
        foreach (var frame in _nullPath) frame.GenerateCode(method, writer);
        writer.FinishBlock();

        writer.WriteComment("A completed saga keeps its stream, and is treated like one that no longer exists");
        writer.Write($"BLOCK:else if ({Saga.Usage}.{nameof(global::Wolverine.Saga.IsCompleted)}())");
        foreach (var frame in _completedPath) frame.GenerateCode(method, writer);
        writer.FinishBlock();

        writer.Write("BLOCK:else");
        foreach (var frame in _existsPath) frame.GenerateCode(method, writer);
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
        => throw new NotSupportedException("Event-sourced sagas are not supported in F# generated code yet (GH-5415).");

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _stream;

        foreach (var path in new[] { _nullPath, _completedPath, _existsPath })
        {
            foreach (var frame in path)
            {
                foreach (var variable in frame.FindVariables(chain))
                {
                    if (ReferenceEquals(variable, Saga) || variable.Creator == this) continue;
                    if (path.Any(x => x.Creates.Contains(variable))) continue;
                    yield return variable;
                }
            }
        }
    }
}

/// <summary>After the saga's methods ran: <see cref="EventSourcedSagaSupport.AfterHandling{TSaga}" /></summary>
internal class AfterEventSourcedSagaHandlingFrame : SyncFrame
{
    private readonly Variable _stream;
    private readonly Variable _saga;
    private readonly bool _archiveWhenCompleted;

    public AfterEventSourcedSagaHandlingFrame(Variable stream, Variable saga, bool archiveWhenCompleted)
    {
        _stream = stream;
        _saga = saga;
        _archiveWhenCompleted = archiveWhenCompleted;
        uses.Add(stream);
        uses.Add(saga);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("A saga completed with MarkCompleted() gets a SagaCompleted event");
        writer.Write(
            $"{typeof(EventSourcedSagaSupport).FullNameInCode()}.{nameof(EventSourcedSagaSupport.AfterHandling)}({_stream.Usage}, {_saga.Usage}, {(_archiveWhenCompleted ? "true" : "false")});");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>A message that only starts the saga, for a saga that already exists</summary>
internal class RefuseStartOfExistingSagaFrame : SyncFrame
{
    private readonly Type _sagaType;
    private readonly Variable _sagaId;

    public RefuseStartOfExistingSagaFrame(Type sagaType, Variable sagaId)
    {
        _sagaType = sagaType;
        _sagaId = sagaId;
        uses.Add(sagaId);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"throw {typeof(EventSourcedSagaSupport).FullNameInCode()}.{nameof(EventSourcedSagaSupport.StartOfExistingSaga)}(typeof({_sagaType.FullNameInCode()}), {_sagaId.Usage});");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// The commit of a saga's new stream: a store's refusal to start a stream another message started first is reported
/// as the <see cref="SagaConcurrencyException" /> a duplicate saga start is
/// </summary>
internal class TranslateStreamCollisionFrame : Frame
{
    private readonly Frame _commit;
    private readonly Type _sagaType;
    private readonly Variable _sagaId;

    public TranslateStreamCollisionFrame(Frame commit, Type sagaType, Variable sagaId) : base(commit.IsAsync)
    {
        _commit = commit;
        _sagaType = sagaType;
        _sagaId = sagaId;
        uses.Add(sagaId);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _sagaId;
        foreach (var variable in _commit.FindVariables(chain)) yield return variable;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var support = typeof(EventSourcedSagaSupport).FullNameInCode();
        writer.Write("BLOCK:try");
        _commit.GenerateCode(method, writer);
        writer.FinishBlock();
        writer.Write(
            $"BLOCK:catch ({typeof(Exception).FullNameInCode()} streamCollision) when ({support}.{nameof(EventSourcedSagaSupport.IsStreamCollision)}(streamCollision))");
        writer.Write(
            $"throw {support}.{nameof(EventSourcedSagaSupport.StreamCollision)}(typeof({_sagaType.FullNameInCode()}), {_sagaId.Usage}, streamCollision);");
        writer.FinishBlock();
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>In an event-sourced saga's <c>Fault&lt;T&gt;</c> chain, compensate the failed message onto the stream</summary>
internal class CompensateEventSourcedSagaFrame : AsyncFrame
{
    private readonly Variable _stream;
    private readonly Type _faultType;
    private Variable? _fault;
    private Variable? _context;

    public CompensateEventSourcedSagaFrame(Variable stream, Type faultType)
    {
        _stream = stream;
        _faultType = faultType;
        uses.Add(stream);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _fault = chain.FindVariable(_faultType);
        yield return _fault;

        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var sagaType = _stream.VariableType.GetGenericArguments()[0];
        var failedType = SagaCompensations.FaultMessageType(_faultType)!;
        writer.WriteComment("Compensate the failed message with the saga's best matching Compensate method, onto its stream");
        writer.Write(
            $"await {typeof(EventSourcedSagaSupport).FullNameInCode()}.{nameof(EventSourcedSagaSupport.CompensateAsync)}<{sagaType.FullNameInCode()}, {failedType.FullNameInCode()}>({_stream.Usage}, {_fault!.Usage}, {_context!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// What the methods of an event-sourced saga return: messages (<see cref="OutgoingMessages" />, a
/// <see cref="TimeoutMessage" />, anything else Wolverine sends itself) are sent, and everything else is an event for the
/// saga's stream -- one value, or a collection of them
/// </summary>
internal class EventSourcedSagaReturnActionSource : IReturnVariableActionSource
{
    private readonly Type _sagaType;
    private readonly string _storeName;

    public EventSourcedSagaReturnActionSource(Type sagaType, string storeName)
    {
        _sagaType = sagaType;
        _storeName = storeName;
    }

    public IReturnVariableAction Build(IChain chain, Variable variable)
    {
        var type = variable.VariableType;

        if (type == typeof(OutgoingMessages)) return new CascadeMessage(variable);

        if (type == typeof(IAsyncEnumerable<object>))
        {
            var frame = new ApplyEventsFromAsyncEnumerableFrame(variable, _storeName, _sagaType);
            return frame;
        }

        if (type.CanBeCastTo<IEnumerable<object>>() && type != typeof(string))
        {
            var action = new ReturnVariableAction { Description = "Append events to the saga's stream" };
            action.Frames.Add(new RegisterEventsFrame(variable, _sagaType).WrapIfNotNull(variable));
            return action;
        }

        if (type.CanBeCastTo<IWolverineReturnType>()) return new CascadeMessage(variable);

        return new EventCaptureActionSource.ActionSource(_sagaType, variable);
    }
}

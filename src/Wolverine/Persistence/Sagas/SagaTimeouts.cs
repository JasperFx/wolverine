using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Runtime;
using Wolverine.Util;

// ReSharper disable once CheckNamespace
namespace Wolverine
{
    /// <summary>
    /// GH-5674. A saga timeout of this type supersedes the earlier timeouts of the same type for the same saga: when a
    /// saga schedules a new one -- a billing period that changed, a deadline that moved -- the earlier one that is still
    /// pending never reaches the saga's <c>Handle</c> method. Keyed by the message type; override
    /// <see cref="TimeoutMessage.TimeoutKey" /> instead to share one key between several timeout types, or to key by
    /// something else.
    /// </summary>
    /// <remarks>
    /// Wolverine keeps the latest timeout id for each key with the saga's state (<see cref="Saga.LatestTimeoutIds" />,
    /// or an event of its own on an event-sourced saga's stream) and stamps it on the scheduled envelope. A superseded
    /// timeout that fires is logged at Debug and discarded before <c>Handle</c> runs. Where the message store can, the
    /// superseded scheduled message is also deleted when its replacement is scheduled.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ReplacesEarlierTimeoutsAttribute : Attribute;

    /// <summary>
    /// GH-5674. Wolverine's own event: an <see cref="EventSourcedSaga" /> scheduled the timeout <paramref name="TimeoutId" />
    /// under <paramref name="Key" />, superseding any earlier timeout with that key. Registered with the event store as
    /// <c>wolverine_saga_timeout_scheduled</c>.
    /// </summary>
    public sealed record SagaTimeoutScheduled(string Key, Guid TimeoutId);
}

namespace Wolverine.Persistence.Sagas
{
    /// <summary>
    /// GH-5674. Timeouts that replace earlier ones: called by <see cref="TimeoutMessage" /> and by generated saga code.
    /// Not for application code.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class SagaTimeouts
    {
        /// <summary>The key a replacing timeout was scheduled under</summary>
        public const string KeyHeader = "wolverine.saga.timeout-key";

        /// <summary>The id of a replacing timeout, compared with the latest one the saga keeps for the key</summary>
        public const string IdHeader = "wolverine.saga.timeout-id";

        private static readonly ConcurrentDictionary<Type, bool> _replaces = new();
        private static readonly ConcurrentDictionary<Type, bool> _sagas = new();

        /// <summary>The key <paramref name="timeout" /> replaces earlier timeouts under, or null if it does not</summary>
        public static string? KeyFor(TimeoutMessage timeout)
        {
            var explicitKey = timeout.TimeoutKey;
            if (explicitKey.IsNotEmpty()) return explicitKey;

            return timeout.GetType().HasAttribute<ReplacesEarlierTimeoutsAttribute>()
                ? timeout.GetType().ToMessageTypeName()
                : null;
        }

        /// <summary>
        /// Does a timeout of this type replace earlier ones: marked <see cref="ReplacesEarlierTimeoutsAttribute" />, or
        /// declaring its own <see cref="TimeoutMessage.TimeoutKey" />?
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Saga message types are rooted by handler discovery.")]
        public static bool Replaces(Type messageType)
            => _replaces.GetOrAdd(messageType, type =>
                type.CanBeCastTo<TimeoutMessage>() &&
                (type.HasAttribute<ReplacesEarlierTimeoutsAttribute>() || declaresTimeoutKey(type)));

        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Saga message types are rooted by handler discovery.")]
        private static bool declaresTimeoutKey(Type type)
            => type.GetProperty(nameof(TimeoutMessage.TimeoutKey),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetMethod?.DeclaringType != typeof(TimeoutMessage);

        /// <summary>Does this saga type handle a timeout that replaces earlier ones?</summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Saga types are rooted by handler discovery.")]
        public static bool SagaUsesReplacingTimeouts(Type sagaType)
            => _sagas.GetOrAdd(sagaType, type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(x => x.GetParameters().FirstOrDefault()?.ParameterType)
                .Any(x => x != null && Replaces(x)));

        /// <summary>
        /// Schedule a replacing timeout: stamped with its key and id, and noted on the context so the saga that sent it
        /// keeps the id as the latest for the key
        /// </summary>
        public static async ValueTask ScheduleAsync(TimeoutMessage timeout, string key, MessageContext context)
        {
            var options = new DeliveryOptions { ScheduleDelay = timeout.DelayTime };
            options.Headers[KeyHeader] = key;

            // A placeholder until the envelope exists: the timeout's id is its scheduled envelope's id, so the stale
            // scheduled message can be deleted by id when this one is replaced
            var placeholder = Guid.NewGuid();
            options.Headers[IdHeader] = placeholder.ToString();

            await context.PublishAsync(timeout, options).ConfigureAwait(false);

            var envelopes = context.Outstanding.Concat(context.Scheduled)
                .Where(x => x.HasHeaders && x.Headers.TryGetValue(IdHeader, out var id) && id == placeholder.ToString())
                .ToArray();

            var timeoutId = envelopes.Length == 0 ? placeholder : envelopes[0].Id;
            foreach (var envelope in envelopes) envelope.Headers[IdHeader] = timeoutId.ToString();

            context.ScheduledSagaTimeouts ??= [];
            context.ScheduledSagaTimeouts.Add(new SagaTimeoutScheduled(key, timeoutId));
        }

        /// <summary>
        /// After a document saga's handler ran: keep the latest id of every replacing timeout it scheduled, and note the
        /// ids those supersede so their scheduled messages can be deleted once the saga is saved
        /// </summary>
        public static void Record(Saga? saga, MessageContext context)
        {
            var scheduled = context.ScheduledSagaTimeouts;
            if (saga == null || scheduled is not { Count: > 0 }) return;

            context.ScheduledSagaTimeouts = null;
            foreach (var timeout in scheduled)
            {
                noteSuperseded(saga, timeout, context);
                saga.LatestTimeoutIds ??= new Dictionary<string, Guid>();
                saga.LatestTimeoutIds[timeout.Key] = timeout.TimeoutId;
            }
        }

        /// <summary>
        /// After an event-sourced saga's handler ran: one <see cref="SagaTimeoutScheduled" /> event per replacing timeout
        /// it scheduled, from which <see cref="EventSourcedSaga" /> keeps the latest ids
        /// </summary>
        public static void RecordEventSourced<TSaga>(IEventStream<TSaga> stream, MessageContext context)
            where TSaga : EventSourcedSaga
        {
            var scheduled = context.ScheduledSagaTimeouts;
            if (scheduled is not { Count: > 0 }) return;

            context.ScheduledSagaTimeouts = null;
            foreach (var timeout in scheduled)
            {
                if (stream.Aggregate != null) noteSuperseded(stream.Aggregate, timeout, context);
                stream.AppendOne(timeout);
            }
        }

        private static void noteSuperseded(Saga saga, SagaTimeoutScheduled timeout, MessageContext context)
        {
            if (saga.LatestTimeoutIds == null || !saga.LatestTimeoutIds.TryGetValue(timeout.Key, out var earlier)) return;
            if (earlier == timeout.TimeoutId) return;

            // The timeout being handled right now has already left the schedule
            if (context.Envelope != null && (context.Envelope.Id == earlier || idOf(context.Envelope) == earlier)) return;

            context.SupersededSagaTimeouts ??= [];
            context.SupersededSagaTimeouts.Add(earlier);
        }

        private static Guid? idOf(Envelope envelope)
            => envelope.HasHeaders && envelope.Headers.TryGetValue(IdHeader, out var raw) && Guid.TryParse(raw, out var id)
                ? id
                : null;

        /// <summary>
        /// The headers that identify a replacing timeout, for a tracked session that replays a scheduled message by
        /// handing its message to the bus again. Null for any other envelope
        /// </summary>
        internal static DeliveryOptions? ReplayOptionsFor(Envelope envelope)
        {
            if (!envelope.HasHeaders || !envelope.Headers.TryGetValue(KeyHeader, out var key)) return null;

            var options = new DeliveryOptions();
            options.Headers[KeyHeader] = key;
            if (envelope.Headers.TryGetValue(IdHeader, out var id)) options.Headers[IdHeader] = id;
            return options;
        }

        /// <summary>
        /// Is the timeout being handled superseded: the saga keeps a later timeout id for its key? Logged at Debug
        /// </summary>
        public static bool IsSuperseded(Saga saga, MessageContext context)
        {
            var envelope = context.Envelope;
            if (envelope == null || !envelope.HasHeaders) return false;
            if (!envelope.Headers.TryGetValue(KeyHeader, out var key) || key.IsEmpty()) return false;
            if (idOf(envelope) is not { } id) return false;

            if (saga.LatestTimeoutIds == null || !saga.LatestTimeoutIds.TryGetValue(key!, out var latest) || latest == id)
            {
                return false;
            }

            context.Runtime.Logger.LogDebug(
                "Discarded timeout {MessageType} {TimeoutId} for saga {SagaType}: a later timeout {LatestTimeoutId} replaced it under the key '{TimeoutKey}'",
                envelope.Message?.GetType().FullNameInCode(), id, saga.GetType().FullNameInCode(), latest, key);
            return true;
        }

        /// <summary>
        /// Once the saga is saved: delete the scheduled messages of the timeouts it superseded, where the message store
        /// can. A failure is only logged; the superseded timeout is discarded when it fires anyway
        /// </summary>
        public static async Task CancelSupersededAsync(MessageContext context)
        {
            var superseded = context.SupersededSagaTimeouts;
            if (superseded is not { Count: > 0 }) return;

            context.SupersededSagaTimeouts = null;
            try
            {
                await context.Storage.ScheduledMessages.CancelAsync(
                    new ScheduledMessageQuery { MessageIds = superseded.ToArray(), PageSize = superseded.Count },
                    context.Runtime.Cancellation).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                context.Runtime.Logger.LogDebug(e,
                    "Could not delete superseded saga timeouts {TimeoutIds}; they are discarded when they fire",
                    superseded);
            }
        }
    }

    /// <summary>
    /// GH-5674. After a saga's handler ran, keep the latest id of each replacing timeout it scheduled: on the saga for a
    /// document saga, as an event on its stream for an event-sourced one
    /// </summary>
    internal class RecordSagaTimeoutsFrame : SyncFrame
    {
        private readonly Variable _target;
        private readonly bool _eventSourced;
        private Variable? _context;

        public RecordSagaTimeoutsFrame(Variable sagaOrStream, bool eventSourced)
        {
            _target = sagaOrStream;
            _eventSourced = eventSourced;
            uses.Add(sagaOrStream);
        }

        public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
        {
            _context = chain.FindVariable(typeof(MessageContext));
            yield return _context;
        }

        private string methodName => _eventSourced ? nameof(SagaTimeouts.RecordEventSourced) : nameof(SagaTimeouts.Record);

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.WriteComment("Keep the latest id of each timeout that replaces earlier ones");
            writer.Write($"{typeof(SagaTimeouts).FullNameInCode()}.{methodName}({_target.Usage}, {_context!.Usage});");
            Next?.GenerateCode(method, writer);
        }

        public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.WriteLine("// Keep the latest id of each timeout that replaces earlier ones");
            writer.Write($"{typeof(SagaTimeouts).FSharpName()}.{methodName}({_target.FSharpUsage}, {_context!.Usage})");
            Next?.GenerateFSharpCode(method, writer);
        }
    }

    /// <summary>
    /// GH-5674. In the chain of a timeout that replaces earlier ones: a superseded timeout reaches neither the saga's
    /// methods nor its storage
    /// </summary>
    internal class SupersededTimeoutGuardFrame : Frame
    {
        private readonly Variable _saga;
        private readonly Frame[] _inner;
        private Variable? _context;

        public SupersededTimeoutGuardFrame(Variable saga, Frame[] inner) : base(true)
        {
            _saga = saga;
            _inner = inner;
            uses.Add(saga);
        }

        public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
        {
            _context = chain.FindVariable(typeof(MessageContext));
            yield return _context;

            foreach (var frame in _inner)
            {
                foreach (var variable in frame.FindVariables(chain))
                {
                    if (_inner.Any(x => x.Creates.Contains(variable))) continue;
                    yield return variable;
                }
            }
        }

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.WriteComment("A timeout replaced by a later one is discarded");
            writer.Write(
                $"BLOCK:if (!{typeof(SagaTimeouts).FullNameInCode()}.{nameof(SagaTimeouts.IsSuperseded)}({_saga.Usage}, {_context!.Usage}))");
            foreach (var frame in _inner) frame.GenerateCode(method, writer);
            writer.FinishBlock();
            Next?.GenerateCode(method, writer);
        }

        public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.WriteLine("// A timeout replaced by a later one is discarded");
            writer.Write(
                $"BLOCK:if not ({typeof(SagaTimeouts).FSharpName()}.{nameof(SagaTimeouts.IsSuperseded)}({_saga.FSharpUsage}, {_context!.Usage})) then");
            foreach (var frame in _inner) frame.GenerateFSharpCode(method, writer);
            writer.FinishBlock();
            Next?.GenerateFSharpCode(method, writer);
        }
    }

    /// <summary>GH-5674. Once the saga is saved, delete the scheduled messages of the timeouts it superseded</summary>
    internal class CancelSupersededTimeoutsFrame : AsyncFrame
    {
        private Variable? _context;

        public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
        {
            _context = chain.FindVariable(typeof(MessageContext));
            yield return _context;
        }

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.Write(
                $"await {typeof(SagaTimeouts).FullNameInCode()}.{nameof(SagaTimeouts.CancelSupersededAsync)}({_context!.Usage}).ConfigureAwait(false);");
            Next?.GenerateCode(method, writer);
        }

        public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.WriteLine(
                $"do! {typeof(SagaTimeouts).FSharpName()}.{nameof(SagaTimeouts.CancelSupersededAsync)}({_context!.Usage})");
            Next?.GenerateFSharpCode(method, writer);
        }
    }
}

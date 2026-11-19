using System.ComponentModel;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

// ReSharper disable once CheckNamespace
namespace Wolverine
{
    /// <summary>
    /// GH-5674. What Wolverine does with a message for a saga that does not exist -- it completed, timed out, or never
    /// started -- when the saga has no <c>NotFound</c> method for that message.
    /// </summary>
    public enum SagaMissingBehavior
    {
        /// <summary>
        /// Throw <see cref="Persistence.Sagas.UnknownSagaException" />, which the message's error policies handle. The
        /// default, because a message for a saga that never existed is usually a real bug
        /// </summary>
        Throw,

        /// <summary>Log the message at Information and drop it. For answers that may arrive after the saga ended</summary>
        Discard
    }

    /// <summary>
    /// GH-5674. A message for a saga that is gone is logged at Information and dropped instead of failing with
    /// <see cref="Persistence.Sagas.UnknownSagaException" />. Put it on the message type, or on the saga's
    /// <c>Handle()</c> method for that message: the method itself never runs when the saga is missing, so there it sets
    /// the policy for that message's saga chain.
    /// </summary>
    /// <remarks>
    /// A <c>NotFound</c> method for the message always wins. A completed saga that is retained
    /// (<c>RetainCompletedSagas()</c>) or event-sourced counts as missing. A message that starts the saga is not
    /// affected, and neither is a saga's compensation (<c>Fault&lt;T&gt;</c>) chain. See also
    /// <see cref="SagaMissingAttribute" /> for one saga type and <c>opts.Policies.WhenSagaIsMissing()</c> for the
    /// application.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method)]
    public sealed class DiscardIfSagaMissingAttribute : Attribute;

    /// <summary>
    /// GH-5674. What Wolverine does with any message for this saga type that arrives after the saga is gone, unless
    /// the saga has a <c>NotFound</c> method for it or the message (or its <c>Handle()</c> method) is marked
    /// <see cref="DiscardIfSagaMissingAttribute" />. Wins over <c>opts.Policies.WhenSagaIsMissing()</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class SagaMissingAttribute : Attribute
    {
        public SagaMissingAttribute(SagaMissingBehavior behavior)
        {
            Behavior = behavior;
        }

        public SagaMissingBehavior Behavior { get; }
    }
}

namespace Wolverine.Persistence.Sagas
{
    /// <summary>
    /// GH-5674. Called by generated saga code for a message whose saga is missing and is to be discarded. Not for
    /// application code.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class SagaMissing
    {
        public static void Discard(MessageContext context, Type sagaType, object sagaId)
        {
            context.Runtime.Logger.LogInformation(
                "Discarded {MessageType} {EnvelopeId}: saga {SagaType} with id {SagaId} no longer exists",
                context.Envelope?.Message?.GetType().FullNameInCode(), context.Envelope?.Id, sagaType.FullNameInCode(),
                sagaId);
        }
    }

    /// <summary>GH-5674. The message's saga is missing, and the message is discarded rather than failed.</summary>
    internal class DiscardMessageForMissingSagaFrame : SyncFrame
    {
        private readonly Type _sagaType;
        private readonly Variable _sagaId;
        private Variable? _context;

        public DiscardMessageForMissingSagaFrame(Type sagaType, Variable sagaId)
        {
            _sagaType = sagaType;
            _sagaId = sagaId;
            uses.Add(sagaId);
        }

        public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
        {
            _context = chain.FindVariable(typeof(MessageContext));
            yield return _context;
        }

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.WriteComment("The saga is gone, and this message is discarded rather than failed");
            writer.Write(
                $"{typeof(SagaMissing).FullNameInCode()}.{nameof(SagaMissing.Discard)}({_context!.Usage}, typeof({_sagaType.FullNameInCode()}), {_sagaId.Usage});");
            Next?.GenerateCode(method, writer);
        }

        public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.WriteLine("// The saga is gone, and this message is discarded rather than failed");
            writer.Write(
                $"{typeof(SagaMissing).FSharpName()}.{nameof(SagaMissing.Discard)}({_context!.Usage}, typeof<{_sagaType.FSharpName()}>, box {_sagaId.FSharpUsage})");
            Next?.GenerateFSharpCode(method, writer);
        }
    }
}

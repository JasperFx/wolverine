using System.Diagnostics;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Sagas;

public class IndeterminateSagaStateIdException : Exception
{
    public IndeterminateSagaStateIdException(Envelope envelope) : base(
        $"Could not determine a valid saga state id for Envelope {envelope}")
    {
    }

    /// <summary>
    /// GH-4531: the overload the generated code uses. Names the saga, the member Wolverine looked at,
    /// and both ways the id could have arrived, because none of that is recoverable from the envelope
    /// alone and all three are things the reader can act on.
    /// </summary>
    /// <param name="envelope">The message envelope being handled</param>
    /// <param name="sagaType">The saga type this chain handles</param>
    /// <param name="sagaIdMemberName">
    /// The message member Wolverine reads the saga id from, or null when the message has none and only the
    /// envelope's <c>SagaId</c> header could have supplied it
    /// </param>
    public IndeterminateSagaStateIdException(Envelope envelope, Type sagaType, string? sagaIdMemberName) : base(
        buildMessage(envelope, sagaType, sagaIdMemberName))
    {
    }

    private static string buildMessage(Envelope envelope, Type sagaType, string? sagaIdMemberName)
    {
        var messageType = envelope.Message?.GetType().FullNameInCode()
                          ?? envelope.MessageType
                          ?? "(unknown)";

        var lead =
            $"Could not determine a saga id for message {messageType} (envelope {envelope.Id}) handled by saga {sagaType.FullNameInCode()}. ";

        var looked = sagaIdMemberName == null
            ? $"The message has no saga identity member -- none marked [SagaIdentity], and none named SagaId, {sagaType.Name}Id or Id -- so the envelope's SagaId header was the only source, and it was missing or could not be parsed. "
            : $"Wolverine looked at the message member '{sagaIdMemberName}' (a member marked [SagaIdentity], or named SagaId / {sagaType.Name}Id / Id) and the envelope's SagaId header; both were missing or default. ";

        return lead + looked +
               "Set the id on the command, or ensure the SagaId header is propagated when the message is sent from outside a saga.";
    }
}

public class UnknownSagaException : Exception
{
    public UnknownSagaException(Type sagaStateType, object stateId) : base(
        $"Could not find an expected saga document of type {sagaStateType.FullNameInCode()} for id '{stateId}'. Note: new Sagas will not be available in storage until the first message succeeds.")
    {

    }
}

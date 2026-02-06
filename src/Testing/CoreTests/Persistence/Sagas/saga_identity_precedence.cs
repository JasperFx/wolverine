using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence.Sagas;

// Because of https://github.com/JasperFx/wolverine/issues/2095
public class saga_identity_precedence : IntegrationContext
{
    public saga_identity_precedence(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task use_the_correct_saga_id_from_message()
    {
        var startMsg = new StartParent("ParentId", "SubId");

        // Create the saga
        var startSession = await Host.TrackActivity().PublishMessageAndWaitAsync(startMsg);
        startSession.FindEnvelopesWithMessageType<StartSub>();

        var comSession = await Host.TrackActivity()
            .PublishMessageAndWaitAsync(new SendComToSubSaga(startMsg.ParentSagaId, startMsg.SubSagaId));

        comSession.FindSingleTrackedMessageOfType<SubSagaMsg>();
        comSession.FindSingleTrackedMessageOfType<ParentSagaMsg>();
    }
    
    [Fact]
    public async Task use_the_correct_saga_id_from_message_int_saga_id_type()
    {
        var startMsg = new StartParent2(1, 2);

        // Create the saga
        var startSession = await Host.TrackActivity().PublishMessageAndWaitAsync(startMsg);
        startSession.FindEnvelopesWithMessageType<StartSub2>();

        var comSession = await Host.TrackActivity()
            .PublishMessageAndWaitAsync(new SendComToSubSaga2(startMsg.ParentSagaId, startMsg.SubSagaId));

        comSession.FindSingleTrackedMessageOfType<SubSagaMsg2>();
        comSession.FindSingleTrackedMessageOfType<ParentSagaMsg2>();
    }
}

public sealed record StartParent(string ParentSagaId, string SubSagaId);
public sealed record StartSub(string ParentSagaId, string SubSagaId);
public sealed record SendComToSubSaga(string ParentSagaId, string SubSagaId);
public sealed record SubSagaMsg([property: SagaIdentity] string SubSagaId);
public sealed record ParentSagaMsg([property: SagaIdentity] string ParentSagaId);

public class ParentSaga : Saga
{
    public required string Id { get; set; }

    public static (ParentSaga, StartSub) Start(StartParent msg, Logger<ParentSaga> logger)
    {
        logger.LogInformation("ParentSaga Id: {SagaIdentity}", msg.ParentSagaId);
        return (new ParentSaga { Id = msg.ParentSagaId }, new StartSub(msg.ParentSagaId, msg.SubSagaId));
    }

    public static void NotFound(ParentSagaMsg msg, Envelope envelope, Logger<ParentSaga> logger)
    {
        logger.LogInformation("NotFound(ParentSagaMsg): Envelope saga identity {EnvelopeIdentity}; message {@Msg}", envelope.SagaId, msg);
    }

    public void Handle(
        [SagaIdentityFrom(nameof(ParentSagaMsg.ParentSagaId))] ParentSagaMsg msg,
        Envelope envelope,
        Logger<ParentSaga> logger
    )
    {
        logger.LogInformation("ParentSagaMsg: Envelope saga identity {EnvelopeIdentity}; message {@Msg}", envelope.SagaId, msg);
    }

    public SubSagaMsg Handle([SagaIdentityFrom(nameof(SendComToSubSaga.ParentSagaId))] SendComToSubSaga msg)
    {
        return new SubSagaMsg(msg.SubSagaId);
    }
}

public class SubSaga : Saga
{
    public required string Id { get; set; }
    public required string ParentId { get; set; }

    public static SubSaga Start(StartSub msg, Logger<SubSaga> logger)
    {
        logger.LogInformation("SubSaga Id: {SagaIdentity}", msg.SubSagaId);
        return new SubSaga { Id = msg.SubSagaId, ParentId = msg.ParentSagaId };
    }

    public static void NotFound(SubSagaMsg msg, Envelope envelope, Logger<SubSaga> logger)
    {
        logger.LogInformation("NotFound(SubSagaMsg): Envelope saga identity {EnvelopeIdentity}; message {@Msg}", envelope.SagaId, msg);
    }

    public ParentSagaMsg Handle([SagaIdentityFrom(nameof(SubSagaMsg.SubSagaId))] SubSagaMsg msg, Envelope envelope, Logger<SubSaga> logger)
    {
        logger.LogInformation("SubSagaMsg: Envelope saga identity {EnvelopeIdentity}; message {@Msg}", envelope.SagaId, msg);

        return new ParentSagaMsg(ParentId);
    }
}


/******** Numbered **************/

public sealed record StartParent2(int ParentSagaId, int SubSagaId);
public sealed record StartSub2(int ParentSagaId, int SubSagaId);
public sealed record SendComToSubSaga2(int ParentSagaId, int SubSagaId);
public sealed record SubSagaMsg2([property: SagaIdentity] int SubSagaId);
public sealed record ParentSagaMsg2([property: SagaIdentity] int ParentSagaId);

public class ParentSaga2 : Saga
{
    public required int Id { get; set; }

    public static (ParentSaga2, StartSub2) Start(StartParent2 msg, Logger<ParentSaga> logger)
    {
        logger.LogInformation("ParentSaga Id: {SagaIdentity}", msg.ParentSagaId);
        return (new ParentSaga2 { Id = msg.ParentSagaId }, new StartSub2(msg.ParentSagaId, msg.SubSagaId));
    }

    public static void NotFound(ParentSagaMsg2 msg, Envelope envelope, Logger<ParentSaga> logger)
    {
        logger.LogInformation("NotFound(ParentSagaMsg): Envelope saga identity {EnvelopeIdentity}; message {@Msg}", envelope.SagaId, msg);
    }

    public void Handle(
        [SagaIdentityFrom(nameof(ParentSagaMsg.ParentSagaId))] ParentSagaMsg2 msg,
        Envelope envelope,
        Logger<ParentSaga2> logger
    )
    {
        logger.LogInformation("ParentSagaMsg: Envelope saga identity {EnvelopeIdentity}; message {@Msg}", envelope.SagaId, msg);
    }

    public SubSagaMsg2 Handle([SagaIdentityFrom(nameof(SendComToSubSaga.ParentSagaId))] SendComToSubSaga2 msg)
    {
        return new SubSagaMsg2(msg.SubSagaId);
    }
}

public class SubSaga2 : Saga
{
    public required int Id { get; set; }
    public required int ParentId { get; set; }

    public static SubSaga2 Start(StartSub2 msg, Logger<SubSaga> logger)
    {
        logger.LogInformation("SubSaga Id: {SagaIdentity}", msg.SubSagaId);
        return new SubSaga2 { Id = msg.SubSagaId, ParentId = msg.ParentSagaId };
    }

    public static void NotFound(SubSagaMsg2 msg, Envelope envelope, Logger<SubSaga2> logger)
    {
        logger.LogInformation("NotFound(SubSagaMsg): Envelope saga identity {EnvelopeIdentity}; message {@Msg}", envelope.SagaId, msg);
    }

    public ParentSagaMsg2 Handle([SagaIdentityFrom(nameof(SubSagaMsg.SubSagaId))] SubSagaMsg2 msg, Envelope envelope, Logger<SubSaga> logger)
    {
        logger.LogInformation("SubSagaMsg: Envelope saga identity {EnvelopeIdentity}; message {@Msg}", envelope.SagaId, msg);

        return new ParentSagaMsg2(ParentId);
    }
}
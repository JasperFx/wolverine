using Marten;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

namespace WolverineWebApi.Marten;

/// <summary>
/// GH-4741. Endpoints that open a Marten session and report back what the session actually knows about the
/// authenticated user. That is the thing <c>docs/guide/http/security.md</c> promises ("automatically set as
/// <c>IDocumentSession.LastModifiedBy</c>") and that nothing used to test: the pre-existing
/// <c>/user/name</c> endpoint takes an <c>IMessageContext</c> and opens no session at all.
/// </summary>
public static class UserNameRelayEndpoints
{
    /// <summary>
    /// Cause 1 of GH-4741: this chain declares neither <c>IMessageContext</c> nor <c>IMessageBus</c>, so the
    /// old <c>UserNamePolicy</c> never inserted its middleware frame and the relay never ran at all.
    /// </summary>
    [WolverineGet("/user/name/marten/session")]
    public static string SessionOnly(IDocumentSession session)
    {
        return session.LastModifiedBy ?? "NONE";
    }

    /// <summary>
    /// Cause 2 of GH-4741: the policy DID fire for this shape, and the relay still did not reach the
    /// session. The session is opened by a hoisted <c>IVariableSource</c> frame, so a frame inserted at
    /// <c>chain.Middleware[0]</c> still ran after <c>OpenSession(messageContext)</c> had already copied a
    /// null into <c>LastModifiedBy</c>.
    /// </summary>
    [WolverineGet("/user/name/marten/session-and-context")]
    public static string SessionAndContext(IDocumentSession session, IMessageContext context)
    {
        return $"{context.UserName ?? "NONE"}|{session.LastModifiedBy ?? "NONE"}";
    }

    /// <summary>
    /// The shape from the issue itself: a pure function returning an <c>IMartenOp</c>, with no session and
    /// no message bus anywhere in the signature. The side effect writes down what the session it is handed
    /// knows, so the assertion survives a round trip through the database.
    /// </summary>
    [WolverinePost("/user/name/marten/op")]
    public static IMartenOp RecordThroughMartenOp(RecordUserName command)
    {
        return new RecordUserNameOp(command.Id);
    }

    [WolverineGet("/user/name/marten/op/{id}")]
    public static async Task<string> ReadRecordedUserName(Guid id, IQuerySession session,
        CancellationToken cancellationToken)
    {
        var record = await session.LoadAsync<UserNameRecord>(id, cancellationToken);
        return record?.UserName ?? "MISSING";
    }
}

public record RecordUserName(Guid Id);

public class UserNameRecord
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = string.Empty;
}

public class RecordUserNameOp : IMartenOp
{
    private readonly Guid _id;

    public RecordUserNameOp(Guid id)
    {
        _id = id;
    }

    public void Execute(IDocumentSession session)
    {
        session.Store(new UserNameRecord { Id = _id, UserName = session.LastModifiedBy ?? "NONE" });
    }
}

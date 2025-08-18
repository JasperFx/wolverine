using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.ComplianceTests.Fakes;

public class GenericFakeTransactionAttribute : ModifyChainAttribute
{
    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        chain.Middleware.Add(new FakeTransaction());
    }
}

public class FakeTransactionAttribute : ModifyHandlerChainAttribute
{
    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain.Middleware.Add(new FakeTransaction());
    }
}

public class FakeTransaction : Frame
{
    private readonly Variable _session;
    private Variable _store;

    public FakeTransaction() : base(false)
    {
        _session = new Variable(typeof(IFakeSession), "session", this);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _store = chain.FindVariable(typeof(IFakeStore));
        yield return _store;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"BLOCK:using (var {_session.Usage} = {_store.Usage}.OpenSession())");
        Next?.GenerateCode(method, writer);
        writer.Write($"{_session.Usage}.{nameof(IFakeSession.SaveChanges)}();");
        writer.FinishBlock();
    }
}

public interface IFakeStore
{
    IFakeSession OpenSession();
}

public class Tracking
{
    public bool CalledSaveChanges;
    public bool DisposedTheSession;
    public bool OpenedSession;
}

public class FakeStore : IFakeStore
{
    private readonly Tracking _tracking;

    public FakeStore(Tracking tracking)
    {
        _tracking = tracking;
    }

    public IFakeSession OpenSession()
    {
        _tracking.OpenedSession = true;
        return new FakeSession(_tracking);
    }
}

public interface IFakeSession : IDisposable
{
    void SaveChanges();
}

public class FakeSession : IFakeSession
{
    private readonly Tracking _tracking;

    public FakeSession(Tracking tracking)
    {
        _tracking = tracking;
    }

    public void Dispose()
    {
        _tracking.DisposedTheSession = true;
    }

    public void SaveChanges()
    {
        _tracking.CalledSaveChanges = true;
    }
}
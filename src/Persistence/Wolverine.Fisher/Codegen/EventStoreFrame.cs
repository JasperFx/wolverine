using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Fisher;
using Fisher.Events;

namespace Wolverine.Fisher.Codegen;

internal class EventStoreFrame : SyncFrame
{
    private readonly Variable _eventStore;
    private Variable? _session;

    public EventStoreFrame()
    {
        _eventStore = Create<EventOperations>();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"var {_eventStore.Usage} = {_session!.Usage}.{nameof(IDocumentSession.Events)};");
        Next?.GenerateCode(method, writer);
    }
}

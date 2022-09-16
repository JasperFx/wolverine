using System.Collections.Generic;
using LamarCodeGeneration;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Marten;
using Marten.Events;

namespace Wolverine.Persistence.Marten.Codegen;

internal class EventStoreFrame : SyncFrame
{
    private Variable? _session;
    private readonly Variable _eventStore;

    public EventStoreFrame()
    {
        _eventStore = Create<IEventStore>();
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



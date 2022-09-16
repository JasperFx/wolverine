using System.Diagnostics;
using Baseline.Reflection;
using Wolverine.Persistence.Marten.Codegen;
using Wolverine.Persistence.Marten.Persistence.Sagas;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Persistence.Marten;

internal class MartenIntegration : IWolverineExtension
{
    /// <summary>
    /// This directs the Marten integration to try to publish events out of the enrolled outbox
    /// for a Marten session on SaveChangesAsync()
    /// </summary>
    public bool ShouldPublishEvents { get; set; }

    public void Configure(WolverineOptions options)
    {
        options.Advanced.CodeGeneration.Sources.Add(new MartenBackedPersistenceMarker());

        var frameProvider = new MartenSagaPersistenceFrameProvider();
        options.Advanced.CodeGeneration.SetSagaPersistence(frameProvider);
        options.Advanced.CodeGeneration.SetTransactions(frameProvider);

        options.Advanced.CodeGeneration.Sources.Add(new SessionVariableSource());

        options.Handlers.Discovery(x => x.IncludeTypes(type => type.HasAttribute<MartenCommandWorkflowAttribute>()));
    }
}

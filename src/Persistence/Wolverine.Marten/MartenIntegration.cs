using JasperFx.Core.Reflection;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Marten;

internal class MartenIntegration : IWolverineExtension
{
    /// <summary>
    ///     This directs the Marten integration to try to publish events out of the enrolled outbox
    ///     for a Marten session on SaveChangesAsync()
    /// </summary>
    public bool ShouldPublishEvents { get; set; }

    public void Configure(WolverineOptions options)
    {
        options.Node.CodeGeneration.Sources.Add(new MartenBackedPersistenceMarker());

        var frameProvider = new MartenSagaPersistenceFrameProvider();
        options.Node.CodeGeneration.SetSagaPersistence(frameProvider);
        options.Node.CodeGeneration.SetTransactions(frameProvider);

        options.Node.CodeGeneration.Sources.Add(new SessionVariableSource());

        options.Handlers.Discovery(x => x.IncludeTypes(type => type.HasAttribute<MartenCommandWorkflowAttribute>()));
    }
}
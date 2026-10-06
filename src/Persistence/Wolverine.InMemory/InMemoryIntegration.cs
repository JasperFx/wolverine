using JasperFx.Events.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.InMemory.Codegen;
using Wolverine.Persistence.Sagas;

namespace Wolverine.InMemory;

/// <summary>
/// Wires the JasperFx.Events.InMemory prototyping store into Wolverine's code generation (wolverine#4838).
/// </summary>
internal class InMemoryIntegration : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.Services.TryAddSingleton<InMemorySessionFactory>();

        options.CodeGeneration.InsertFirstPersistenceStrategy<InMemoryPersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new InMemorySessionSource());
        options.CodeGeneration.Sources.Add(new SessionContractSource());
    }
}

public static class WolverineOptionsInMemoryExtensions
{
    /// <summary>
    /// Use the in-memory prototyping store (JasperFx.Events.InMemory) for this application's documents
    /// and events, so its handlers run before you've chosen Marten, Polecat or Fisher.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For prototyping, not production.</b> Nothing is persisted, the host refuses to start in the
    /// <c>Production</c> environment unless you opt in, and there is no durable inbox or outbox:
    /// messaging runs in memory.
    /// </para>
    /// <para>
    /// Only Wolverine's store-agnostic usage is supported -- <c>[WriteModel]</c> / <c>[ReadModel]</c>,
    /// <c>Storage.StartStream</c> / <c>AppendEvents</c>, <c>[Entity]</c>, <c>IStorageAction&lt;T&gt;</c>
    /// and sagas -- so the same handlers run unchanged once you switch to a real store.
    /// </para>
    /// </remarks>
    public static WolverineOptions UseInMemoryStoreForPrototyping(this WolverineOptions options,
        Action<InMemoryStoreOptions>? configure = null)
    {
        options.Services.AddInMemoryStoreForPrototyping(configure);
        options.Include<InMemoryIntegration>();
        return options;
    }
}

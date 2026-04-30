using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

// Marks this assembly as having a source-generated IWolverineTypeLoader.
// Mirrors what the Wolverine.SourceGeneration analyzer emits when it processes
// a project. Used by tests covering #2632 (aggregating manifests across
// referenced assemblies, not just Options.ApplicationAssembly).
[assembly: WolverineTypeManifest(typeof(TypeLoaderManifestModuleA.ModuleATypeLoader))]

namespace TypeLoaderManifestModuleA;

public record ModuleAMessage(string Name);

public class ModuleAHandler
{
    public void Handle(ModuleAMessage message)
    {
    }
}

public class ModuleATypeLoader : IWolverineTypeLoader
{
    public IReadOnlyList<Type> DiscoveredHandlerTypes { get; } = new[] { typeof(ModuleAHandler) };

    public IReadOnlyList<(Type MessageType, string Alias)> DiscoveredMessageTypes { get; } =
        new (Type, string)[] { (typeof(ModuleAMessage), "module-a-message") };

    public IReadOnlyList<Type> DiscoveredHttpEndpointTypes { get; } = Array.Empty<Type>();

    public IReadOnlyList<Type> DiscoveredExtensionTypes { get; } = Array.Empty<Type>();

    public bool HasPreGeneratedHandlers => false;

    public IReadOnlyDictionary<string, Type>? PreGeneratedHandlerTypes => null;

    public Type? TryFindPreGeneratedType(string typeName) => null;
}

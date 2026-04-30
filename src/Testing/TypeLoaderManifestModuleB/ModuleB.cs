using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

[assembly: WolverineTypeManifest(typeof(TypeLoaderManifestModuleB.ModuleBTypeLoader))]

namespace TypeLoaderManifestModuleB;

public record ModuleBMessage(string Name);

public class ModuleBHandler
{
    public void Handle(ModuleBMessage message)
    {
    }
}

public class ModuleBTypeLoader : IWolverineTypeLoader
{
    public IReadOnlyList<Type> DiscoveredHandlerTypes { get; } = new[] { typeof(ModuleBHandler) };

    public IReadOnlyList<(Type MessageType, string Alias)> DiscoveredMessageTypes { get; } =
        new (Type, string)[] { (typeof(ModuleBMessage), "module-b-message") };

    public IReadOnlyList<Type> DiscoveredHttpEndpointTypes { get; } = Array.Empty<Type>();

    public IReadOnlyList<Type> DiscoveredExtensionTypes { get; } = Array.Empty<Type>();

    public bool HasPreGeneratedHandlers => false;

    public IReadOnlyDictionary<string, Type>? PreGeneratedHandlerTypes => null;

    public Type? TryFindPreGeneratedType(string typeName) => null;
}

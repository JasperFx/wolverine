using Wolverine.Runtime.Handlers;

namespace Module2;

// GH-4699 test fixture. HandlerDiscovery.TryLoadStaticHandlerRegistry finds the registry by walking
// WolverineOptions.ApplicationAssembly.ExportedTypes, so proving that TypeLoadMode.Static honors
// DisableConventionalDiscovery() needs a *committed* HandlerRegistry whose captured handler types are
// BROADER than the current discovery configuration allows -- exactly the drift a stale `codegen write`
// leaves behind. That cannot be faked from inside the test assembly.
//
// This lives in Module2 rather than CoreTests on purpose: a public HandlerRegistry subclass silently
// becomes the registry for EVERY host in its assembly that runs Static mode or UseStaticRegistries(),
// and CoreTests has several of those. Module2 is a fixture library that no host ever points
// ApplicationAssembly at except the GH-4699 test, so the blast radius is contained. Keep it the only
// HandlerRegistry in this assembly -- TryLoadStaticHandlerRegistry takes the first one it finds.

public record Gh4699IncludedMessage;

public record Gh4699ExcludedMessage;

// Deliberately named so that conventional discovery does NOT find these: no "Handler"/"Consumer" name
// suffix, no [WolverineHandler], not a Saga. Module2 is marked [WolverineHandlerModule], so anything
// conventionally discoverable here is picked up by every host in the suite that opts into handler
// modules. These two reach a handler graph only by way of the registry below, or an explicit
// IncludeType.
public class Gh4699IncludedWorker
{
    public void Handle(Gh4699IncludedMessage message)
    {
    }
}

public class Gh4699ExcludedWorker
{
    public void Handle(Gh4699ExcludedMessage message)
    {
    }
}

/// <summary>
///     Stands in for the <c>GeneratedHandlerRegistry</c> that <c>codegen write</c> emits, listing both
///     handler types. A host that also calls <c>DisableConventionalDiscovery().IncludeType&lt;Gh4699IncludedWorker&gt;()</c>
///     must end up with the included handler only.
/// </summary>
public class Gh4699StaticRegistry : HandlerRegistry
{
    public override Type[] HandlerTypes()
    {
        return [typeof(Gh4699IncludedWorker), typeof(Gh4699ExcludedWorker)];
    }

    public override Type[] MessageTypes()
    {
        return [];
    }
}

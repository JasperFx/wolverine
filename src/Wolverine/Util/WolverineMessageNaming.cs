using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;

namespace Wolverine.Util;

public interface IMessageTypeNaming
{
    bool TryDetermineName(Type messageType, out string messageTypeName);
}

internal class WebSocketMessageNaming : IMessageTypeNaming
{
    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        if (messageType.CanBeCastTo<WebSocketMessage>())
        {
            messageTypeName = PascalToKebabCase(messageType.NameInCode());
            return true;
        }

        messageTypeName = default!;
        return false;
    }
    
    public static string PascalToKebabCase(string value)
    {
        return value.SplitPascalCase().Replace(' ', '_').ToLowerInvariant();
    }
}

internal class InteropAttributeForwardingNaming : IMessageTypeNaming
{
    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        if (messageType.TryGetAttribute<InteropMessageAttribute>(out var att))
        {
            messageTypeName = att.InteropType.ToMessageTypeName();
            return true;
        }

        messageTypeName = default!;
        return false;
    }
}

internal class MessageIdentityAttributeNaming : IMessageTypeNaming
{
    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        if (messageType.TryGetAttribute<MessageIdentityAttribute>(out var att))
        {
            messageTypeName = att.GetName()!;

            return true;
        }

        messageTypeName = default!;
        return false;
    }
}

internal class ForwardNaming : IMessageTypeNaming
{
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "messageType originates from Wolverine's message-type registry (HandlerDiscovery / RegisterMessageType). The IForwardsTo<> interface closure inspection runs against application-rooted forwarder types preserved by the registration.")]
    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        if (messageType.Closes(typeof(IForwardsTo<>)))
        {
            var forwardedType = messageType.FindInterfaceThatCloses(typeof(IForwardsTo<>))!.GetGenericArguments()
                .Single();
            messageTypeName = forwardedType.ToMessageTypeName();
            return true;
        }

        messageTypeName = default!;
        return false;
    }
}

internal class FullTypeNaming : IMessageTypeNaming
{
    private static readonly Regex _aliasSanitizer = new("<|>", RegexOptions.Compiled);

    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        var nameToAlias = messageType.FullName;
        if (messageType.IsGenericType)
        {
            nameToAlias = _aliasSanitizer.Replace(messageType.GetPrettyName(), string.Empty);
        }

        var parts = new List<string> { nameToAlias! };
        if (messageType.IsNested)
        {
            parts.Insert(0, messageType.DeclaringType!.Name);
        }

        messageTypeName = string.Join("_", parts).Replace(',', '_');
        return true;
    }
}

internal class InteropAssemblyInterfaces : IMessageTypeNaming
{
    internal List<Assembly> Assemblies { get; } = [];

    // Suppression rather than annotating the IMessageTypeNaming interface +
    // 6 implementations: the messageType comes from runtime-resolved message
    // types that are already kept by virtue of being instantiated. Trimming
    // could in theory remove an interface from messageType's interface list,
    // but the impl is opt-in interop naming — Apps that need it register the
    // assemblies explicitly via opts.AddInteropAssembly(...), which keeps
    // those assemblies' types in the trim graph.
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "InteropAssemblyInterfaces is opt-in interop naming; consumers register assemblies explicitly which preserves the relevant interfaces in the trim graph.")]
    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        var @interface = messageType.GetInterfaces()
            .FirstOrDefault(x => Assemblies.Contains(x.Assembly));

        if (@interface != null)
        {
            messageTypeName = @interface.ToMessageTypeName();
            return true;
        }

        messageTypeName = default!;
        return false;
    }
}

public static class WolverineMessageNaming
{
    private static ImHashMap<Type, string> _typeNames = ImHashMap<Type, string>.Empty;

    private static readonly List<IMessageTypeNaming> _namingStrategies =
    [
        new MessageIdentityAttributeNaming(),
        new WebSocketMessageNaming(),
        new InteropAttributeForwardingNaming(),
        new ForwardNaming(),
        new InteropAssemblyInterfaces(),
        new FullTypeNaming()
    ];

    public static void InsertFirst<T>() where T : IMessageTypeNaming, new()
    {
        if (_namingStrategies[0] is T) return;
        _namingStrategies.Insert(0, new T());
    }

    /// <summary>
    ///     Tag an assembly as containing message types that should be used for interoperability with
    ///     NServiceBus or MassTransit
    /// </summary>
    /// <param name="assembly"></param>
    public static void AddMessageInterfaceAssembly(Assembly assembly)
    {
        var naming = _namingStrategies.OfType<InteropAssemblyInterfaces>().Single();
        naming.Assemblies.Fill(assembly);
    }

    public static string GetPrettyName(this Type t)
    {
        if (!t.IsGenericType)
        {
            return t.Name;
        }

        var name = t.Name[..t.Name.LastIndexOf('`')];
        var generics = t.GetGenericArguments()
            .Aggregate("<",
                (aggregate, type) =>
                {
                    var prettyName = GetPrettyName(type);

                    if (aggregate == "<")
                    {
                        return $"{aggregate}{prettyName}";
                    }

                    return $"{aggregate},{prettyName}";
                });
        return $"{name}{generics}>";
    }

    public static string ToMessageTypeName(this Type type)
    {
        if (_typeNames.TryFind(type, out var alias))
        {
            return alias;
        }

        var name = toMessageTypeName(type);
        _typeNames = _typeNames.AddOrUpdate(type, name);

        return name;
    }

    /// <summary>
    /// Pre-populate the message-type-name cache with the supplied types. Called during
    /// Wolverine startup so the per-message <see cref="ToMessageTypeName(Type)"/> hot
    /// path never pays the first-occurrence reflection cost (attribute reads, interface
    /// walks, generic-type pretty-printing) inside <c>Envelope</c> construction or
    /// dispatch. See issue #1577 (cold-start optimizations).
    /// </summary>
    /// <param name="types">Types to resolve and cache. Duplicates are tolerated.</param>
    public static void PrepopulateCache(IEnumerable<Type> types)
    {
        if (types == null) return;

        foreach (var type in types)
        {
            if (type == null) continue;
            if (_typeNames.TryFind(type, out _)) continue;

            var name = toMessageTypeName(type);
            _typeNames = _typeNames.AddOrUpdate(type, name);
        }
    }

    private static string toMessageTypeName(Type type)
    {
        foreach (var namingStrategy in _namingStrategies)
        {
            if (namingStrategy.TryDetermineName(type, out var name))
            {
                return name;
            }
        }

        return type.Name;
    }
}
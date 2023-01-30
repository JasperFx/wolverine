using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;

namespace Wolverine.Util;

public interface IMessageTypeNaming
{
    bool TryDetermineName(Type messageType, out string messageTypeName);
}

internal class MessageIdentityAttributeNaming : IMessageTypeNaming
{
    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        if (messageType.HasAttribute<MessageIdentityAttribute>())
        {
            var att = messageType.GetAttribute<MessageIdentityAttribute>();
            messageTypeName = att.GetName();

            return true;
        }

        messageTypeName = default;
        return false;
    }
}

internal class ForwardNaming : IMessageTypeNaming
{
    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        if (messageType.Closes(typeof(IForwardsTo<>)))
        {
            var forwardedType = messageType.FindInterfaceThatCloses(typeof(IForwardsTo<>))!.GetGenericArguments().Single();
            messageTypeName = forwardedType.ToMessageTypeName();
            return true;
        }

        messageTypeName = default;
        return false;
    }
}

internal class FullTypeNaming : IMessageTypeNaming
{
    private static readonly Regex _aliasSanitizer = new("<|>", RegexOptions.Compiled);
    
    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        var nameToAlias = messageType.FullName;
        if (messageType.GetTypeInfo().IsGenericType)
        {
            nameToAlias = _aliasSanitizer.Replace(messageType.GetPrettyName(), string.Empty);
        }

        var parts = new List<string> { nameToAlias! };
        if (messageType.IsNested)
        {
            parts.Insert(0, messageType.DeclaringType!.Name);
        }

        messageTypeName = string.Join("_", parts);
        return true;
    }
}

internal class InteropAssemblyInterfaces : IMessageTypeNaming
{
    internal List<Assembly> Assemblies { get; } = new();

    public bool TryDetermineName(Type messageType, out string messageTypeName)
    {
        var @interface = messageType.GetInterfaces()
            .FirstOrDefault(x => Assemblies.Contains(x.Assembly));

        if (@interface != null)
        {
            messageTypeName = @interface.ToMessageTypeName();
            return true;
        }

        messageTypeName = default;
        return false;
    }
}



public static class WolverineMessageNaming
{
    private static ImHashMap<Type, string> _typeNames = ImHashMap<Type, string>.Empty;

    private static readonly List<IMessageTypeNaming> _namingStrategies = new()
    {
        
        new MessageIdentityAttributeNaming(),
        new ForwardNaming(),
        new InteropAssemblyInterfaces(),
        new FullTypeNaming()
    };

    /// <summary>
    /// Tag an assembly as containing message types that should be used for interoperability with
    /// NServiceBus or MassTransit
    /// </summary>
    /// <param name="assembly"></param>
    public static void AddMessageInterfaceAssembly(Assembly assembly)
    {
        var naming = _namingStrategies.OfType<InteropAssemblyInterfaces>().Single();
        naming.Assemblies.Fill(assembly);
    }
    
    
    private static readonly Type[] _tupleTypes =
    {
        typeof(ValueTuple<>),
        typeof(ValueTuple<,>),
        typeof(ValueTuple<,,>),
        typeof(ValueTuple<,,,>),
        typeof(ValueTuple<,,,,>),
        typeof(ValueTuple<,,,,,>),
        typeof(ValueTuple<,,,,,,>),
        typeof(ValueTuple<,,,,,,,>)
    };

    public static string GetPrettyName(this Type t)
    {
        if (!t.GetTypeInfo().IsGenericType)
        {
            return t.Name;
        }

        var sb = new StringBuilder();

        sb.Append(t.Name.Substring(0, t.Name.LastIndexOf("`", StringComparison.Ordinal)));
        sb.Append(t.GetTypeInfo().GetGenericArguments().Aggregate("<",
            (aggregate, type) => aggregate + (aggregate == "<" ? "" : ",") + GetPrettyName(type)));
        sb.Append(">");

        return sb.ToString();
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

    [Obsolete("moved into Core")]
    public static bool IsValueTuple(this Type? type)
    {
        return type is { IsGenericType: true } && _tupleTypes.Contains(type.GetGenericTypeDefinition());
    }
}
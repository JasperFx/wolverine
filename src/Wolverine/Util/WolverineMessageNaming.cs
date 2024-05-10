﻿using System.Reflection;
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
        new InteropAttributeForwardingNaming(),
        new ForwardNaming(),
        new InteropAssemblyInterfaces(),
        new FullTypeNaming()
    ];

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
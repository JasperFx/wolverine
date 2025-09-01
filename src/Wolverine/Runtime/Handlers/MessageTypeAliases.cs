using ImTools;

namespace Wolverine.Runtime.Handlers;

public class MessageTypeAliases
{
    private readonly Func<Type, string> _aliasNaming;
    private ImHashMap<Type, string> _typeToName = ImHashMap<Type, string>.Empty;
    private ImHashMap<string, Type> _aliasToType = ImHashMap<string, Type>.Empty;
    
    public MessageTypeAliases(Func<Type, string> aliasNaming)
    {
        _aliasNaming = aliasNaming;
    }

    public void Register(Type messageType)
    {
        var alias = _aliasNaming(messageType);
        Register(alias, messageType);
    }

    public void Register(string alias, Type messageType)
    {
        _aliasToType = _aliasToType.AddOrUpdate(alias, messageType);
        _typeToName = _typeToName.AddOrUpdate(messageType, alias);
    }

    public void RegisterAlias(string alias, Type messageType)
    {
        _aliasToType = _aliasToType.AddOrUpdate(alias, messageType);
    }

    public string AliasFor(Type messageType)
    {
        if (_typeToName.TryFind(messageType, out var alias))
        {
            return alias;
        }

        alias = _aliasNaming(messageType);
        _typeToName = _typeToName.AddOrUpdate(messageType, alias);

        _aliasToType = _aliasToType.AddOrUpdate(alias, messageType);
        
        return alias;
    }

    public bool TryFindMessageType(string alias, out Type messageType)
    {
        return _aliasToType.TryFind(alias, out messageType);
    }
}
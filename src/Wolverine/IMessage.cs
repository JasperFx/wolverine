namespace Wolverine;

/// <summary>
///     Marker interface to denote that a type is a Wolverine
///     message. This is strictly for diagnostic purposes!
/// </summary>
public interface IMessage
{
}

/// <summary>
///     Marker interface to denote that a type is a Wolverine
///     message. This is strictly for diagnostic purposes!
/// </summary>
public interface IEvent : IMessage
{
}

/// <summary>
///     Marker interface to denote that a type is a Wolverine
///     message. This is strictly for diagnostic purposes!
/// </summary>
public interface ICommand : IMessage
{
}
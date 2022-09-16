namespace Wolverine;

/// <summary>
///     Implement in a message class to "forward" the execution
///     to another message type. This is useful for message versioning
///     and backwards compatibility
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IForwardsTo<T>
{
    T Transform();
}

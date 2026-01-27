namespace Wolverine.Persistence.Sagas;

/// <summary>
///     Marks a public property on a message type handler parameter as the saga state identity
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class SagaIdentityFromAttribute(string propertyName) : Attribute
{
    public string PropertyName { get => propertyName; }
}
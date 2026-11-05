namespace Wolverine.Persistence.Sagas;

/// <summary>
///     Marks a handler or HTTP endpoint method whose returned <see cref="Saga"/> is only a value to hand back
///     to the caller (an HTTP response body, for example) and is NOT a new saga to start. By default Wolverine
///     treats any returned <see cref="Saga"/> as a new saga and inserts it, which fails with a duplicate key
///     when the saga was already started some other way (e.g. by IMessageBus.InvokeAsync()).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class SagaIsResponseOnlyAttribute : Attribute;

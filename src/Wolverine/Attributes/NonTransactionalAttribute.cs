namespace Wolverine.Attributes;

/// <summary>
///     Explicitly opts out a handler or HTTP endpoint from having transactional
///     middleware applied automatically by <c>AutoApplyTransactions()</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class NonTransactionalAttribute : Attribute;

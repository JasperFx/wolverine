namespace Wolverine.Polecat;

/// <summary>
/// Marks a property or field on a command type as the aggregate identity
/// for the Polecat aggregate handler workflow.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class IdentityAttribute : Attribute;

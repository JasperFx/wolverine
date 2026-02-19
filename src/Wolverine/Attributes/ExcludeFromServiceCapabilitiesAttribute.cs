namespace Wolverine.Attributes;

/// <summary>
/// When applied at the assembly level, tells Wolverine to exclude all message types
/// and handlers from this assembly when building ServiceCapabilities descriptions.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class ExcludeFromServiceCapabilitiesAttribute : Attribute;

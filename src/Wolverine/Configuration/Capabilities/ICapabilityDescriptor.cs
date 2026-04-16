using JasperFx.Descriptors;

namespace Wolverine.Configuration.Capabilities;

/// <summary>
/// Implement this interface to contribute additional capability descriptions
/// to ServiceCapabilities. Frameworks like Wolverine.HTTP can register
/// implementations in DI to surface their own configuration and route data.
/// </summary>
public interface ICapabilityDescriptor
{
    /// <summary>
    /// Build an OptionsDescription representing this capability.
    /// The description will be added to ServiceCapabilities.AdditionalCapabilities.
    /// </summary>
    OptionsDescription Describe();
}

using JasperFx.Core;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    private readonly IList<Type> _extensionTypes = new List<Type>();
    internal List<IWolverineExtension> AppliedExtensions { get; } = [];

    
    /// <summary>
    ///     Applies the extension to this application
    /// </summary>
    /// <param name="extension"></param>
    public void Include(IWolverineExtension extension)
    {
        ApplyExtensions([extension]);
    }

    internal void ApplyExtensions(IWolverineExtension[] extensions)
    {
        // Apply idempotency
        extensions = extensions.Where(x => !_extensionTypes.Contains(x.GetType())).ToArray();

        foreach (var extension in extensions)
        {
            try
            {
                extension.Configure(this);
            }
            catch (InvalidOperationException e)
            {
                if (e.Message.Contains("The service collection cannot be modified because it is read-only."))
                {
                    throw new InvalidOperationException(
                        "As of Wolverine 3.0, it's no longer supported to alter IoC service registrations through Wolverine extensions that are themselves registered in the IoC container",
                        e);
                }
                
                throw;
            }
            AppliedExtensions.Add(extension);
        }

        _extensionTypes.Fill(extensions.Select(x => x.GetType()));
    }

    /// <summary>
    ///     Applies the extension with optional configuration to the application
    /// </summary>
    /// <param name="configure">Optional configuration of the extension</param>
    /// <typeparam name="T"></typeparam>
    public void Include<T>(Action<T>? configure = null) where T : IWolverineExtension, new()
    {
        var extension = new T();
        configure?.Invoke(extension);

        ApplyExtensions([extension]);
    }
}
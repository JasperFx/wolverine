using System.Text.Json;

namespace Wolverine.Runtime.Interop.MassTransit;

public interface IMassTransitInterop
{
    /// <summary>
    ///     Use System.Text.Json as the default JSON serialization with optional configuration
    /// </summary>
    /// <param name="configuration"></param>
    void UseSystemTextJsonForSerialization(Action<JsonSerializerOptions>? configuration = null);

    // Newtonsoft.Json variant moved to WolverineFx.Newtonsoft as the
    // UseNewtonsoftForSerialization(this IMassTransitInterop, ...)
    // extension method. Install WolverineFx.Newtonsoft to opt in.
}

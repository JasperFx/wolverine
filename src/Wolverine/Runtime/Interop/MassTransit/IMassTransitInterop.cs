using System;
using System.Text.Json;
using Newtonsoft.Json;

namespace Wolverine.Runtime.Interop.MassTransit;

public interface IMassTransitInterop
{
    /// <summary>
    ///     Use System.Text.Json as the default JSON serialization with optional configuration
    /// </summary>
    /// <param name="configuration"></param>
    void UseSystemTextJsonForSerialization(Action<JsonSerializerOptions>? configuration = null);

    /// <summary>
    ///     Use Newtonsoft.Json as the default JSON serialization with optional configuration
    /// </summary>
    /// <param name="configuration"></param>
    void UseNewtonsoftForSerialization(Action<JsonSerializerSettings>? configuration = null);
}
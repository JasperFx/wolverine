using MemoryPack;
using Wolverine.Configuration;
using Wolverine.MemoryPack.Internal;

namespace Wolverine.MemoryPack;

public static class WolverineMemoryPackSerializationExtensions
{
    /// <summary>
    ///     Make MemoryPack the default serializer for this application
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configuration"></param>
    public static void UseMemoryPackSerialization(this WolverineOptions options,
        Action<MemoryPackSerializerOptions>? configuration = null)
    {
        var serializerOptions = MemoryPackSerializerOptions.Default;

        configuration?.Invoke(serializerOptions);

        var serializer = new MemoryPackMessageSerializer(serializerOptions);

        options.DefaultSerializer = serializer;
    }

    /// <summary>
    ///     Apply MemoryPack serialization for just this endpoint
    /// </summary>
    /// <param name="listener"></param>
    /// b
    /// <param name="configuration"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T UseMemoryPackSerialization<T>(this T endpoint,
        Action<MemoryPackSerializerOptions>? configuration = null) where T : IEndpointConfiguration<T>
    {
        var serializerOptions = MemoryPackSerializerOptions.Default;

        configuration?.Invoke(serializerOptions);

        var serializer = new MemoryPackMessageSerializer(serializerOptions);
        endpoint.DefaultSerializer(serializer);
        return endpoint;
    }
}
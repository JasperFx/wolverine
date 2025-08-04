using Wolverine.Configuration;
using Wolverine.Protobuf.Internal;

namespace Wolverine.Protobuf;

public static class WolverineProtobufSerializationExtensions
{
    /// <summary>
    ///     Make Protobuf the default serializer for this application
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configuration"></param>
    public static void UseProtobufSerialization(this WolverineOptions options,
        Action<ProtobufSerializerOptions>? configuration = null)
    {
        var serializerOptions = ProtobufSerializerOptions.Standard;

        configuration?.Invoke(serializerOptions);

        var serializer = new ProtobufMessageSerializer(serializerOptions);

        options.DefaultSerializer = serializer;
    }

    /// <summary>
    ///     Apply Protobuf serialization for just this endpoint
    /// </summary>
    /// <param name="listener"></param>
    /// b
    /// <param name="configuration"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T UseProtobufSerialization<T>(this T endpoint,
        Action<ProtobufSerializerOptions>? configuration = null) where T : IEndpointConfiguration<T>
    {
        var serializerOptions = ProtobufSerializerOptions.Standard;

        configuration?.Invoke(serializerOptions);

        var serializer = new ProtobufMessageSerializer(serializerOptions);
        endpoint.DefaultSerializer(serializer);
        return endpoint;
    }
}

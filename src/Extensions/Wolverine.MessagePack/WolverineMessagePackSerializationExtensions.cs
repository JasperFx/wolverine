using MessagePack;
using Wolverine.Configuration;
using Wolverine.MessagePack.Internal;

namespace Wolverine.MessagePack;

public static class WolverineMessagePackSerializationExtensions
{
    /// <summary>
    ///     Make MessagePack the default serializer for this application
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configuration"></param>
    public static void UseMessagePackSerialization(this WolverineOptions options,
        Action<MessagePackSerializerOptions>? configuration = null)
    {
        var serializerOptions = MessagePackSerializerOptions.Standard;

        configuration?.Invoke(serializerOptions);

        var serializer = new MessagePackMessageSerializer(serializerOptions);

        options.DefaultSerializer = serializer;
    }

    /// <summary>
    ///     Apply MessagePack serialization for just this endpoint
    /// </summary>
    /// <param name="listener"></param>
    /// b
    /// <param name="configuration"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T UseMessagePackSerialization<T>(this T endpoint,
        Action<MessagePackSerializerOptions>? configuration = null) where T : IEndpointConfiguration<T>
    {
        var serializerOptions = MessagePackSerializerOptions.Standard;

        configuration?.Invoke(serializerOptions);

        var serializer = new MessagePackMessageSerializer(serializerOptions);
        endpoint.DefaultSerializer(serializer);
        return endpoint;
    }
}

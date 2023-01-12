using MemoryPack;
using Wolverine.MemoryPack.Internal;

namespace Wolverine.MemoryPack;

public static class WolverineMemoryPackSerializationExtensions
{
    public static void UseMemoryPackSerialization(this WolverineOptions options, Action<MemoryPackSerializerOptions>? configuration = null)
    {
        var serializerOptions = MemoryPackSerializerOptions.Default;

        configuration?.Invoke(serializerOptions);

        var serializer = new MemoryPackMessageSerializer(serializerOptions);

        options.DefaultSerializer = serializer;
    }
}
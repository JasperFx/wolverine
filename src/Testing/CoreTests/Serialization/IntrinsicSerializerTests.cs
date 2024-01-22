using System.Text;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Serialization;

public class IntrinsicSerializerTests
{
    [Fact]
    public void round_trip_serialization()
    {
        var serializer = new IntrinsicSerializer<SerializedMessage>();
        var message = new SerializedMessage { Name = "Sarah Jarosz" };
        var bytes = serializer.WriteMessage(message);
        var message2 = serializer.ReadFromData(bytes).ShouldBeOfType<SerializedMessage>();
        
        message2.ShouldBeEquivalentTo(message);
    }
}

public class SerializedMessage : ISerializable
{
    public string Name { get; set; } = "Bob Schneider";
    
    public byte[] Write()
    {
        return Encoding.Default.GetBytes(Name);
    }

    public static object Read(byte[] bytes)
    {
        var name = Encoding.Default.GetString(bytes);
        return new SerializedMessage { Name = name };
    }
}
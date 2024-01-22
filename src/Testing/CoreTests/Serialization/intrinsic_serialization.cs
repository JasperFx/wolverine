using System.Text;
using NSubstitute;
using TestingSupport;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Serialization;

public class intrinsic_serialization
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

#region sample_intrinsic_serialization

public class SerializedMessage : ISerializable
{
    public string Name { get; set; } = "Bob Schneider";
    
    public byte[] Write()
    {
        return Encoding.Default.GetBytes(Name);
    }

    // You'll need at least C# 11 for static methods
    // on interfaces!
    public static object Read(byte[] bytes)
    {
        var name = Encoding.Default.GetString(bytes);
        return new SerializedMessage { Name = name };
    }
}

#endregion
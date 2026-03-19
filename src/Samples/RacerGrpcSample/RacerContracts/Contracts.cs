using ProtoBuf;
using ProtoBuf.Grpc;
using System.ServiceModel;

namespace RacerContracts;

/// <summary>
/// A speed update sent from a racer (client) to the race server.
/// </summary>
[ProtoContract]
public class RacerUpdate
{
    /// <summary>Unique identifier for this racer.</summary>
    [ProtoMember(1)]
    public string RacerId { get; set; } = string.Empty;

    /// <summary>Current speed in km/h.</summary>
    [ProtoMember(2)]
    public double Speed { get; set; }
}

/// <summary>
/// A position update streamed back from the race server to the client.
/// </summary>
[ProtoContract]
public class RacePosition
{
    /// <summary>Racer identifier.</summary>
    [ProtoMember(1)]
    public string RacerId { get; set; } = string.Empty;

    /// <summary>Current position in the race (1 = leading).</summary>
    [ProtoMember(2)]
    public int Position { get; set; }

    /// <summary>Current speed of this racer in km/h.</summary>
    [ProtoMember(3)]
    public double Speed { get; set; }
}

/// <summary>
/// Code-first gRPC service contract that demonstrates bidirectional streaming.
///
/// The client sends a stream of <see cref="RacerUpdate"/> messages (each racer reports its speed).
/// The server computes current standings and streams back a <see cref="RacePosition"/> per update
/// received.
///
/// In protobuf-net.Grpc, bidirectional streaming is expressed naturally with
/// <see cref="IAsyncEnumerable{T}"/>: an <c>IAsyncEnumerable&lt;T&gt;</c> parameter is the
/// client-to-server stream; an <c>IAsyncEnumerable&lt;T&gt;</c> return type is the
/// server-to-client stream.
/// </summary>
[ServiceContract]
public interface IRacingService
{
    /// <summary>
    /// Bidirectional streaming race method.  For every <see cref="RacerUpdate"/> the client
    /// sends, the server streams back the racer's updated <see cref="RacePosition"/>.
    /// </summary>
    [OperationContract]
    IAsyncEnumerable<RacePosition> RaceAsync(
        IAsyncEnumerable<RacerUpdate> updates,
        CallContext context = default);
}

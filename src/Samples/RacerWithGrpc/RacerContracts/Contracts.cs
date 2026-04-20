using System.ServiceModel;
using ProtoBuf;
using ProtoBuf.Grpc;

namespace RacerContracts;

/// <summary>
///     A speed update sent from a racer (client) to the race server.
/// </summary>
[ProtoContract]
public class RacerUpdate
{
    [ProtoMember(1)]
    public string RacerId { get; set; } = string.Empty;

    /// <summary>Current speed in km/h.</summary>
    [ProtoMember(2)]
    public double Speed { get; set; }
}

/// <summary>
///     A position update streamed back from the race server to the client.
/// </summary>
[ProtoContract]
public class RacePosition
{
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
///     Code-first gRPC contract demonstrating <b>bidirectional streaming</b>.
///     The client sends a stream of <see cref="RacerUpdate"/> messages; the server computes
///     current standings and streams back a <see cref="RacePosition"/> per update received.
///     <para>
///         In protobuf-net.Grpc, bidirectional streaming is expressed naturally:
///         an <c>IAsyncEnumerable&lt;T&gt;</c> parameter is the client-to-server stream and
///         an <c>IAsyncEnumerable&lt;T&gt;</c> return type is the server-to-client stream.
///     </para>
/// </summary>
[ServiceContract]
public interface IRacingService
{
    [OperationContract]
    IAsyncEnumerable<RacePosition> RaceAsync(
        IAsyncEnumerable<RacerUpdate> updates,
        CallContext context = default);
}

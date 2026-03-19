#region sample_racer_client_bidirectional_streaming

using System.Runtime.CompilerServices;
using Grpc.Net.Client;
using ProtoBuf.Grpc.Client;
using RacerContracts;

// Development only — allows plain HTTP/2 without TLS.
// In production configure a TLS certificate on the server and use https://.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var serverUrl = args.Length > 0 ? args[0] : "http://localhost:5300";

Console.WriteLine($"RacerClient connecting to {serverUrl}");
Console.WriteLine("Starting race — press Ctrl+C to stop.\n");

using var channel = GrpcChannel.ForAddress(serverUrl);
var racing = channel.CreateGrpcService<IRacingService>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Simulate three racers sending speed updates in round-robin order.
var racerIds = new[] { "Racer-A", "Racer-B", "Racer-C" };
var rng = new Random();

// Build the outgoing (client → server) update stream.
async IAsyncEnumerable<RacerUpdate> ProduceUpdates(
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        foreach (var id in racerIds)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            yield return new RacerUpdate
            {
                RacerId = id,
                Speed = Math.Round(rng.NextDouble() * 50 + 150, 1) // 150–200 km/h
            };
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }
}

// Consume the server's (server → client) position-update stream.
await foreach (var position in racing.RaceAsync(ProduceUpdates(cts.Token), cts.Token))
{
    Console.WriteLine($"  {position.RacerId,10}  position={position.Position}  speed={position.Speed:F1} km/h");
}

Console.WriteLine("\nRace finished.");

#endregion

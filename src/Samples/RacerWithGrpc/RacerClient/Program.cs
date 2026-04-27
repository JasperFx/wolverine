#region sample_racer_client_bidirectional_streaming

using System.Runtime.CompilerServices;
using Grpc.Net.Client;
using ProtoBuf.Grpc.Client;
using RacerContracts;

// Development only — allows plain HTTP/2 without TLS.
// In production configure a TLS certificate on the server and use https://.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var serverUrl = args.Length > 0 ? args[0] : "http://localhost:5004";

Console.WriteLine($"RacerClient connecting to {serverUrl}");
Console.WriteLine("Starting race — press Ctrl+C to stop.\n");

using var channel = GrpcChannel.ForAddress(serverUrl);
var racing = channel.CreateGrpcService<IRacingService>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var racerIds = new[] { "Racer-A", "Racer-B", "Racer-C" };
var rng = new Random();

// Client → server: round-robin speed updates for three racers.
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

// Server → client: full standings snapshot streamed back on every racer update.
// Each snapshot starts with position=1; print a divider to separate them visually.
await foreach (var position in racing.RaceAsync(ProduceUpdates(cts.Token), cts.Token))
{
    if (position.Position == 1)
        Console.WriteLine("\n--- Standings ---");

    Console.WriteLine($"  #{position.Position}  {position.RacerId,-10}  {position.Speed,6:F1} km/h");
}

Console.WriteLine("\nRace finished.");

#endregion

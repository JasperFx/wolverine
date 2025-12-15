using JasperFx.CommandLine;
using LoadTesting.Trips;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace LoadTesting;

// local://loadtesting.trips.starttrip/

[Description("Build lots of data in the inbox")]
public class SeedCommand : JasperFxAsyncCommand<NetCoreInput>
{
    public override async Task<bool> Execute(NetCoreInput input)
    {
        using var host = input.BuildHost();

        var runtime = host.GetRuntime();

        await runtime.Storage.Admin.MigrateAsync();

        for (int i = 0; i < 50000; i++)
        {
            var streams = TripStream.RandomStreams(100);
            var envelopes = new List<Envelope>();
            
            if (streams[0].TryCheckoutCommand(out var message1))
            {
                var envelope = new Envelope
                {
                    Message = message1,
                    Destination = new Uri("rabbitmq://queue/LoadTesting.Trips.ContinueTrip"),
                    Serializer = runtime.Options.DefaultSerializer,
                    ContentType = "application/json",
                    Status = EnvelopeStatus.Incoming,
                    OwnerId = 0
                };
                    
                envelopes.Add(envelope);
                    
                    
            }
            
            foreach (var tripStream in streams.Skip(1))
            {
                if (tripStream.TryCheckoutCommand(out var message))
                {
                    var envelope = new Envelope
                    {
                        Message = message,
                        Destination = new Uri("rabbitmq://queue/LoadTesting.Trips.StartTrip"),
                        Serializer = runtime.Options.DefaultSerializer,
                        ContentType = "application/json",
                        Status = EnvelopeStatus.Handled
                    };
                    
                    envelopes.Add(envelope);
                    
                    
                }
            }

            await runtime.Storage.Inbox.StoreIncomingAsync(envelopes);

            if (i % 1000 == 0)
            {
                Console.WriteLine("Published " + i);
            }
        }

        return true;
    }
}
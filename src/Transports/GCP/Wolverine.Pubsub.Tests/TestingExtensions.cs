using Google.Api.Gax;

namespace Wolverine.Pubsub.Tests;

public static class TestingExtensions {
    public static PubsubConfiguration UsePubsubTesting(this WolverineOptions options) {
        // options.Policies.Add(new LambdaEndpointPolicy<PubsubEndpoint>((e, _) => {
        //     e.Mapper = new TestPubsubEnvelopeMapper(e);
        // }));

        return options.UsePubsub(Environment.GetEnvironmentVariable("PUBSUB_PROJECT_ID") ?? throw new NullReferenceException(), opts => {
            opts.EmulatorDetection = EmulatorDetection.EmulatorOnly;
        });
    }
}

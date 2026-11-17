// Each test class starts a broker or emulator container (Pulsar, LocalStack, the gcloud Pub/Sub emulator),
// and those are heavy enough that running them one at a time is the better trade.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

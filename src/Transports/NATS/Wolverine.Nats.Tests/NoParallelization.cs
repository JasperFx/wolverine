using Xunit;

// Disable parallelization for NATS tests to avoid subject conflicts
[assembly: CollectionBehavior(DisableTestParallelization = true)]

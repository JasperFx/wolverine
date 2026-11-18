global using Shouldly;
global using Xunit;
global using Bobcat.Xunit;

// Each fixture is a host of its own, but they share one PostgreSQL server: run one at a time
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

global using Xunit;
global using Shouldly;
global using JasperFx.Core;

// Scenarios share databases, broker queues and static script state, so they run one at a time
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

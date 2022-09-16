using System.Runtime.CompilerServices;
using Wolverine.Attributes;
using Lamar;
using Oakton;

[assembly: IgnoreAssembly]
[assembly: BaselineTypeDiscovery.IgnoreAssembly]
[assembly: OaktonCommandAssembly]
[assembly: WolverineFeature]

[assembly: InternalsVisibleTo("CoreTests")]
[assembly: InternalsVisibleTo("CircuitBreakingTests")]
[assembly: InternalsVisibleTo("TestingSupport")]
[assembly: InternalsVisibleTo("Wolverine.RabbitMq")]
[assembly: InternalsVisibleTo("Wolverine.Http")]
[assembly: InternalsVisibleTo("Wolverine.RabbitMq.Tests")]
[assembly: InternalsVisibleTo("Wolverine.AzureServiceBus")]
[assembly: InternalsVisibleTo("Wolverine.ConfluentKafka")]
[assembly: InternalsVisibleTo("Wolverine.AzureServiceBus.Tests")]
[assembly: InternalsVisibleTo("Wolverine.Persistence.Testing")]
[assembly: InternalsVisibleTo("Wolverine.Persistence.Database")]
[assembly: InternalsVisibleTo("Wolverine.Persistence.Marten")]
[assembly: InternalsVisibleTo("Wolverine.Persistence.EntityFrameworkCore")]
[assembly: InternalsVisibleTo("Wolverine.Pulsar")]
[assembly: InternalsVisibleTo("Wolverine.Pulsar.Tests")]
[assembly: InternalsVisibleTo("InteroperabilityTests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

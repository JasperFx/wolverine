using System.Runtime.CompilerServices;
using Wolverine.Attributes;
using Lamar;
using Oakton;

[assembly: IgnoreAssembly]
[assembly: BaselineTypeDiscovery.IgnoreAssembly]
[assembly: OaktonCommandAssembly]
[assembly: WolverineFeature]

[assembly: InternalsVisibleTo("CoreTests")]
[assembly: InternalsVisibleTo("PolicyTests")]
[assembly: InternalsVisibleTo("CircuitBreakingTests")]
[assembly: InternalsVisibleTo("TestingSupport")]
[assembly: InternalsVisibleTo("Wolverine.RabbitMq")]
[assembly: InternalsVisibleTo("Wolverine.RabbitMq.Tests")]
[assembly: InternalsVisibleTo("Wolverine.AzureServiceBus")]
[assembly: InternalsVisibleTo("Wolverine.ConfluentKafka")]
[assembly: InternalsVisibleTo("Wolverine.AzureServiceBus.Tests")]
[assembly: InternalsVisibleTo("PersistenceTests")]
[assembly: InternalsVisibleTo("ScheduledJobTests")]
[assembly: InternalsVisibleTo("Wolverine.RDBMS")]
[assembly: InternalsVisibleTo("Wolverine.Marten")]
[assembly: InternalsVisibleTo("Wolverine.EntityFrameworkCore")]
[assembly: InternalsVisibleTo("Wolverine.Pulsar")]
[assembly: InternalsVisibleTo("Wolverine.Pulsar.Tests")]
[assembly: InternalsVisibleTo("InteroperabilityTests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

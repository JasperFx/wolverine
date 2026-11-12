using System.Runtime.CompilerServices;
using Wolverine.Attributes;

[assembly: ExcludeFromServiceCapabilities]

[assembly: InternalsVisibleTo("PersistenceTests")]
[assembly: InternalsVisibleTo("SqlServerTests")]
[assembly: InternalsVisibleTo("MySqlTests")]
[assembly: InternalsVisibleTo("PostgresqlTests")]
[assembly: InternalsVisibleTo("MartenTests")]
[assembly: InternalsVisibleTo("PolecatTests")]
[assembly: InternalsVisibleTo("SqliteTests")]
[assembly: InternalsVisibleTo("Wolverine.Oracle")]
[assembly: InternalsVisibleTo("OracleTests")]
[assembly: InternalsVisibleTo("Wolverine.ComplianceTests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")] // Castle Core proxies for NSubstitute
// GH-5144: the document store integrations share the outbox commit sequence in Wolverine.RDBMS.Outbox
[assembly: InternalsVisibleTo("Wolverine.Marten")]
[assembly: InternalsVisibleTo("Wolverine.Polecat")]
[assembly: InternalsVisibleTo("Wolverine.Fisher")]
[assembly: InternalsVisibleTo("FisherTests")]

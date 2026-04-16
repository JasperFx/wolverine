using System.Runtime.CompilerServices;
using Wolverine.Attributes;

[assembly: ExcludeFromServiceCapabilities]

[assembly: InternalsVisibleTo("Wolverine.Marten")]
[assembly: InternalsVisibleTo("PersistenceTests")]
[assembly: InternalsVisibleTo("MartenTests")]
[assembly: InternalsVisibleTo("PostgresqlTests")]
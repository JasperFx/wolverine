using System.Runtime.CompilerServices;
using Wolverine.Attributes;

[assembly: ExcludeFromServiceCapabilities]

[assembly: InternalsVisibleTo("PersistenceTests")]
[assembly: InternalsVisibleTo("SqlServerTests")]
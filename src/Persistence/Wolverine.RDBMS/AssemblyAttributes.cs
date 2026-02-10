using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PersistenceTests")]
[assembly: InternalsVisibleTo("SqlServerTests")]
[assembly: InternalsVisibleTo("PostgresqlTests")]
[assembly: InternalsVisibleTo("MartenTests")]
[assembly: InternalsVisibleTo("SqliteTests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")] // Castle Core proxies for NSubstitute
global using Xunit;
// Oracle.ManagedDataAccess.Core 23.26 (the floor Weasel.Oracle 10 sets) ships its own
// Oracle.ManagedDataAccess.Client.OracleDataSource. Every test here that names OracleDataSource
// means Wolverine's, and most of them also import the driver namespace for OracleConnection.
global using OracleDataSource = Wolverine.Oracle.OracleDataSource;

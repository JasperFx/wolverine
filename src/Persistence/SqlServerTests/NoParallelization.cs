// GH-5676. One test at a time in this assembly. CollectionPerAssembly alone did not do that: every class marked
// [Collection("sqlserver")] or [Collection("marten")] became a second collection, and xUnit ran it beside the
// default one. Two kinds of flake came from that:
//
//   * sql_server_ordered_envelope_ids asserts Envelope.IdGenerator, which is process-wide. A host started by a
//     test in the other collection (weasel_cli_dump_executes_against_sql_server is a SQL Server-only app, so it
//     switches to SQL Server-ordered ids) overwrote it between the test's own startup and its assertion.
//   * The lightweight saga storage checks its table on first use by reading SQL Server's catalog. DDL from the
//     other collection (clear_all_wolverine_storage drops and recreates its schema) deadlocked that read, and a
//     saga message failed with "chosen as the deadlock victim".
//
// Parallelism comes from the supervisor's worker lanes instead, each with its own database (build/CITargets.cs).
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

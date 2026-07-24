using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Wolverine.Oracle;

namespace OracleTests;

public class oracle_durability_command_translation
{
    [Fact]
    public void rewrites_generic_parameter_markers_for_oracle()
    {
        OracleMessageStore.normalizeParameterMarkers(
                "delete from messages where replayable = @replayable and keep_until <= @p0")
            .ShouldBe(
                "delete from messages where replayable = :replayable and keep_until <= :p0");
    }

    [Fact]
    public void separates_generic_database_batches_into_oracle_statements()
    {
        OracleMessageStore.splitStatements(
                "select destination from outgoing; delete from incoming;")
            .ShouldBe(
            [
                "select destination from outgoing",
                "delete from incoming"
            ]);
    }

    [Fact]
    public void converts_boolean_parameters_to_oracle_number()
    {
        using var command = new OracleCommand();
        command.Parameters.Add(new OracleParameter
        {
            ParameterName = "replayable",
            Value = true
        });

        OracleMessageStore.normalizeParameters(command);

        var parameter = (OracleParameter)command.Parameters["replayable"];
        parameter.OracleDbType.ShouldBe(OracleDbType.Int16);
        parameter.Value.ShouldBe(1);
    }
}

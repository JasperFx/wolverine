using JasperFx.CodeGeneration.Commands;

namespace Wolverine.Http.Tests;

public class can_verify_all_codegen : IntegrationContext
{
    public can_verify_all_codegen(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void try_to_verify_all()
    {
        Host.AssertWolverineConfigurationIsValid();
    }
}
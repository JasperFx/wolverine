using Microsoft.Extensions.Hosting;
using Wolverine;

namespace DocumentationSamples;

public class DiagnosticSamples
{
    #region sample_using_AssertWolverineConfigurationIsValid

    public static void assert_configuration_is_valid(IHost host)
    {
        host.AssertWolverineConfigurationIsValid();
    }

    #endregion
}
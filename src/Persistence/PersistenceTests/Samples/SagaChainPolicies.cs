using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;

namespace PersistenceTests.Samples;

public class SagaChainPolicies
{
    public static async Task configure()
    {
        #region sample_configuring_chain_policy_on_sagas

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.Add<TurnDownLoggingOnSagas>();
            }).StartAsync();

        #endregion
    }
}

#region sample_turn_down_logging_for_sagas

public class TurnDownLoggingOnSagas : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var sagaChain in chains.OfType<SagaChain>())
        {
            sagaChain.ProcessingLogLevel = LogLevel.None;
            sagaChain.SuccessLogLevel = LogLevel.None;
        }
    }
}

#endregion
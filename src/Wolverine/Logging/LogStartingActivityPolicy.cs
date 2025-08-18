using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Logging;

internal class LogStartingActivityPolicy : IHandlerPolicy
{
    private readonly LogLevel _level;

    public LogStartingActivityPolicy(LogLevel level)
    {
        _level = level;
    }

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            chain.Middleware.Insert(0, new LogStartingActivity(_level, chain));
        }
    }
}
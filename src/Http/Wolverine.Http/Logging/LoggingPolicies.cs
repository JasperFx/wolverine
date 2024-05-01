
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Lamar;
using Wolverine.Configuration;
using Wolverine.Logging;

namespace Wolverine.Http.Logging;

public class AuditLoggingPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IContainer container)
        => new Wolverine.Logging.AuditLoggingPolicy().Apply(chains, rules, container);
}

public class AddConstantsToLoggingContextPolicy<TMessage> : IHttpPolicy
{
    readonly (string, object)[] _loggingConstants;

    public AddConstantsToLoggingContextPolicy(params (string, object)[] loggingConstants)
    {
        _loggingConstants = loggingConstants;
    }
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IContainer container)
        => new Wolverine.Logging.AddConstantsToLoggingContextPolicy<TMessage>(_loggingConstants).Apply(chains, rules, container);
}

public static class WolverineHttpOptionsExtensions
{
    public static void AddLoggingConstantsFor<TMessage>(this WolverineHttpOptions options, params (string, object)[] kvp)
    {
        options.Policies.Add(new AddConstantsToLoggingContextPolicy<TMessage>(kvp));
    }

    public static void AddAuditLogging(this WolverineHttpOptions options, params (string, object)[] kvp)
    {
        options.Policies.Add(new AuditLoggingPolicy());
    }
}

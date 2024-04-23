using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Lamar;
using Wolverine.Configuration;

namespace Wolverine.Logging;

public class AuditLoggingPolicy : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IContainer container)
    {
        foreach (var chain in chains)
        {
            List<Frame> frames = [];
            if (!chain.AuditedMembers.Exists(member => member.MemberName.Equals(TenantIdLogContextFrame.TenantIdContextName))
                && !chain.Middleware.Exists(frame => frame is TenantIdLogContextFrame))
            {
                frames.Add(new TenantIdLogContextFrame());
            }
            frames.Add(new LoggerBeginScopeWithAuditFrame(chain, container));
            chain.AddMiddlewareAfterLoggingContextFrame(frames.ToArray());
        }
    }
}

public class AddConstantsToLoggingContextPolicy<TMessage> : IChainPolicy
{
    private readonly (string, object)[] _loggingConstants;

    public AddConstantsToLoggingContextPolicy(params (string, object)[] loggingConstants)
    {
        _loggingConstants = loggingConstants;
    }
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IContainer container)
    {
        foreach (var chain in chains.Where(chain => typeof(TMessage).IsAssignableFrom(chain.InputType())))
        {
            chain.AddMiddlewareAfterLoggingContextFrame(new AddConstantsToLoggingContextFrame(_loggingConstants));
        }
    }
}

public static class WolverineOptionsExtensions
{
    public static void AddLoggingConstantsFor<TMessage>(this WolverineOptions options, params (string, object)[] kvp)
    {
        options.Policies.Add(new AddConstantsToLoggingContextPolicy<TMessage>(kvp));
    }

    public static void AddAuditLogging(this WolverineOptions options, params (string, object)[] kvp)
    {
        options.Policies.Add(new AuditLoggingPolicy());
    }
}

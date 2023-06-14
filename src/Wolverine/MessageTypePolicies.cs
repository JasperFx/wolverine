using System.Linq.Expressions;
using JasperFx.Core.Reflection;
using Wolverine.Logging;

namespace Wolverine;

public class MessageTypePolicies<T>
{
    private readonly WolverineOptions _parent;

    public MessageTypePolicies(WolverineOptions parent)
    {
        _parent = parent;
    }

    /// <summary>
    ///     Specify that the following members on every message that can be cast
    ///     to type T should be audited as part of telemetry, logging, and metrics
    ///     data exported from this application
    /// </summary>
    /// <param name="members"></param>
    /// <typeparam name="T"></typeparam>
    public MessageTypePolicies<T> Audit(params Expression<Func<T, object>>[] memberExpressions)
    {
        var members = memberExpressions.Select(expr => FindMembers.Determine(expr).First()).ToArray();

        var policy = new AuditMembersPolicy<T>(members);
        _parent.RegisteredPolicies.Insert(0, policy);
        return this;
    }

    /// <summary>
    ///     Add middleware only on handlers where the message type can be cast to the message
    ///     type of the middleware type
    /// </summary>
    /// <param name="middlewareType"></param>
    public MessageTypePolicies<T> AddMiddleware(Type middlewareType)
    {
        var policy = _parent.FindOrCreateMiddlewarePolicy();

        policy.AddType(middlewareType, c => c.InputType().CanBeCastTo<T>()).MatchByMessageType = true;
        return this;
    }

    /// <summary>
    ///     Add middleware only on handlers where the message type can be cast to the message
    ///     type of the middleware type
    /// </summary>
    /// <param name="middlewareType"></param>
    public MessageTypePolicies<T> AddMiddleware<TMiddleware>()
    {
        return AddMiddleware(typeof(TMiddleware));
    }
}
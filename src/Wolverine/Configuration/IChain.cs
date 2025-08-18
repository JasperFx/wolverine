using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Configuration;

internal static class ChainExtensions
{
    public static bool MatchesScope(this IChain chain, MethodInfo method)
    {
        if (chain == null) return true;

        if (method.TryGetAttribute<ScopedMiddlewareAttribute>(out var att))
        {
            if (att.Scoping == MiddlewareScoping.Anywhere) return true;

            return att.Scoping == chain.Scoping;
        }

        // All good if no attribute
        return true;
    }
}

#region sample_IChain

/// <summary>
///     Models the middleware arrangement for either an HTTP route execution
///     or the execution of a message
/// </summary>
public interface IChain
{
    MiddlewareScoping Scoping { get; }
    
    void ApplyParameterMatching(MethodCall call);
    
    /// <summary>
    ///     Frames that would be initially placed in front of
    ///     the primary action(s)
    /// </summary>
    List<Frame> Middleware { get; }

    /// <summary>
    ///     Frames that would be initially placed behind the primary
    ///     action(s)
    /// </summary>
    List<Frame> Postprocessors { get; }

    /// <summary>
    ///     A description of this frame
    /// </summary>
    string Description { get; }

    List<AuditedMember> AuditedMembers { get; }
    Dictionary<string, object> Tags { get; }

    /// <summary>
    ///     Strategy for dealing with any return values from the handler methods
    /// </summary>
    IReturnVariableActionSource ReturnVariableActionSource { get; set; }

    /// <summary>
    ///     Used internally by Wolverine for "outbox" mechanics
    /// </summary>
    /// <returns></returns>
    bool ShouldFlushOutgoingMessages();

    bool RequiresOutbox();

    MethodCall[] HandlerCalls();

    /// <summary>
    ///     Find all of the service dependencies of the current chain
    /// </summary>
    /// <param name="container"></param>
    /// <param name="stopAtTypes"></param>
    /// <param name="chain"></param>
    /// <returns></returns>
    IEnumerable<Type> ServiceDependencies(IServiceContainer container, IReadOnlyList<Type> stopAtTypes);

    /// <summary>
    ///     Does this chain have the designated attribute type anywhere in
    ///     its handlers?
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    bool HasAttribute<T>() where T : Attribute;

    /// <summary>
    ///     The input type for this chain
    /// </summary>
    /// <returns></returns>
    Type? InputType();

    /// <summary>
    ///     Add a member of the message type to be audited during execution
    /// </summary>
    /// <param name="member"></param>
    /// <param name="heading"></param>
    void Audit(MemberInfo member, string? heading = null);

    /// <summary>
    ///     Help out the code generation a little bit by telling this chain
    ///     about a service dependency that will be used. Helps connect
    ///     transactional middleware
    /// </summary>
    /// <param name="type"></param>
    public void AddDependencyType(Type type);

    void ApplyImpliedMiddlewareFromHandlers(GenerationRules generationRules);
    
    /// <summary>
    /// Special usage to make the single result of this method call be the actual response type
    /// for the chain. For HTTP, this becomes the resource type written to the response. For message handlers,
    /// this could be part of InvokeAsync<T>() or just a cascading message
    /// </summary>
    /// <param name="methodCall"></param>
    void UseForResponse(MethodCall methodCall);

    /// <summary>
    ///     Find all variables returned by any handler call in this chain
    ///     that can be cast to T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    IEnumerable<Variable> ReturnVariablesOfType<T>();

    /// <summary>
    ///     Find all variables returned by any handler call in this chain
    ///     that can be cast to the supplied type
    /// </summary>
    /// <returns></returns>
    IEnumerable<Variable> ReturnVariablesOfType(Type interfaceType);

    /// <summary>
    /// Used by code generation to find a simple value on input types, headers, route values,
    /// query string, or claims for use in loading other data
    /// </summary>
    /// <param name="valueName"></param>
    /// <param name="source"></param>
    /// <param name="valueType"></param>
    /// <param name="variable"></param>
    /// <returns></returns>
    bool TryFindVariable(string valueName, ValueSource source, Type valueType, out Variable variable);

    /// <summary>
    /// Used by code generation to add a middleware Frame that aborts the processing if the variable is null
    /// </summary>
    /// <param name="variable"></param>
    Frame[] AddStopConditionIfNull(Variable variable);

    /// <summary>
    /// Used by code generation to add a middleware Frame that aborts the processing if the variable is null
    /// </summary>
    /// <param name="variable"></param>
    Frame[] AddStopConditionIfNull(Variable data, Variable? identity, IDataRequirement requirement);
}

#endregion
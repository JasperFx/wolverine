using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Lamar;
using Wolverine.Logging;

namespace Wolverine.Configuration;

#region sample_IChain

/// <summary>
///     Models the middleware arrangement for either an HTTP route execution
///     or the execution of a message
/// </summary>
public interface IChain
{
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
    IEnumerable<Type> ServiceDependencies(IContainer container, IReadOnlyList<Type> stopAtTypes);

    /// <summary>
    /// Does this chain have the designated attribute type anywhere in
    /// its handlers?
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    bool HasAttribute<T>() where T : Attribute;

    /// <summary>
    /// The input type for this chain
    /// </summary>
    /// <returns></returns>
    Type? InputType();

    /// <summary>
    /// Add a member of the message type to be audited during execution
    /// </summary>
    /// <param name="member"></param>
    /// <param name="heading"></param>
    void Audit(MemberInfo member, string? heading = null);

    /// <summary>
    /// Find all variables returned by any handler call in this chain
    /// that can be cast to T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public IEnumerable<Variable> ReturnVariablesOfType<T>() =>
        HandlerCalls().SelectMany(x => x.Creates).Where(x => x.VariableType.CanBeCastTo<T>());

    /// <summary>
    /// Help out the code generation a little bit by telling this chain
    /// about a service dependency that will be used. Helps connect
    /// transactional middleware
    /// </summary>
    /// <param name="type"></param>
    public void AddDependencyType(Type type);
    
    /// <summary>
    /// Strategy for dealing with any return values from the handler methods
    /// </summary>
    IReturnVariableActionSource ReturnVariableActionSource { get; set; }
}

#endregion
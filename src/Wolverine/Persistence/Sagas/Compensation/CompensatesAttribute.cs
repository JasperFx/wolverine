// ReSharper disable once CheckNamespace
namespace Wolverine;

/// <summary>
/// GH-5672. Names message types a saga's <c>Compensate</c> method covers that Wolverine cannot find on its own: a
/// message the saga sends through <c>IMessageBus</c> rather than returns, for instance. Wolverine asks for the
/// <see cref="Fault{T}" /> of each one it sends and compensates it with this method.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Compensate</c> method whose failed-message parameter is an interface or a base type,
/// <c>Compensate(ITransferStep failed, ExceptionInfo ex)</c>, already covers every concrete message type assignable to
/// it that the application knows about: the messages its handlers handle and return, and the message types routed with
/// <c>PublishMessage&lt;T&gt;()</c> or registered with <c>RegisterMessageType()</c>. <c>[Compensates]</c> is for the
/// rest. Each type it names must be assignable to the method's failed-message parameter.
/// </para>
/// <para>
/// A <c>Compensate(object failed, ...)</c> catch-all covers only the messages the saga's own methods return, plus
/// those named here.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CompensatesAttribute : Attribute
{
    public CompensatesAttribute(params Type[] messageTypes)
    {
        MessageTypes = messageTypes ?? [];
    }

    /// <summary>The message types this method compensates, beyond those Wolverine finds itself</summary>
    public Type[] MessageTypes { get; }
}

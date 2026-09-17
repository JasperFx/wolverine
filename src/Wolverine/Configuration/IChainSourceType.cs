namespace Wolverine.Configuration;

/// <summary>
/// Implemented by a chain that knows the user-written type it was built from, for conventions that
/// need to group chains by type or by assembly.
/// </summary>
/// <remarks>
/// <para>
/// GH-4477. <see cref="IChain.HandlerCalls" /> already answers this for handler and HTTP chains -- a
/// handler chain yields its handler methods, an HTTP chain yields its endpoint method -- so neither
/// needs to implement this. The gRPC chains do: <c>HandlerCalls()</c> returns an empty array on all
/// three of them by design, because a gRPC chain delegates to the message bus rather than wrapping a
/// call, and a convention written against <c>HandlerCalls()</c> alone silently no-ops over every gRPC
/// service in the application.
/// </para>
/// <para>
/// <c>IGrpcChain.ServiceType</c> has carried this information since GH-4383, but it lives in
/// Wolverine.Grpc and core cannot reference it. This is the seam core can stand on.
/// </para>
/// </remarks>
public interface IChainSourceType
{
    /// <summary>
    /// The user-written type this chain was built from, as opposed to anything Wolverine generates
    /// from it.
    /// </summary>
    Type SourceType { get; }
}

using Grpc.Net.Client;

namespace Wolverine.Grpc.Client;

/// <summary>
///     Per-client code-first configuration that is not exposed through
///     <see cref="Grpc.Net.ClientFactory.GrpcClientFactoryOptions"/> because code-first typed
///     clients do not ride on <c>IHttpClientFactory</c>. Keyed by typed-client name so multiple
///     code-first registrations can coexist in the same DI container.
/// </summary>
/// <remarks>
///     This is an internal plumbing option — callers should configure a code-first client via
///     <see cref="WolverineGrpcClientBuilder.ConfigureChannel"/>, which writes into this shape.
/// </remarks>
public sealed class WolverineGrpcCodeFirstClientOptions
{
    /// <summary>
    ///     Callbacks applied in registration order to the <see cref="GrpcChannelOptions"/> used
    ///     to construct this client's <see cref="GrpcChannel"/>.
    /// </summary>
    public List<Action<GrpcChannelOptions>> ChannelConfigurations { get; } = new();
}

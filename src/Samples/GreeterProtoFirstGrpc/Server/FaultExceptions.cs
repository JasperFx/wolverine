namespace GreeterProtoFirstGrpc.Server;

/// <summary>
///     Maps a string kind to a .NET exception so the sample's Fault handler can be driven
///     parametrically across every branch of <c>WolverineGrpcExceptionMapper</c>. The gRPC
///     interceptor registered by <c>AddWolverineGrpc</c> converts each one to the canonical
///     gRPC <c>StatusCode</c> per Google AIP-193.
/// </summary>
public static class FaultExceptions
{
    public static Exception Throw(string kind) => kind switch
    {
        "argument" => new ArgumentException("bad argument"),
        "key" => new KeyNotFoundException("missing key"),
        "file" => new FileNotFoundException("no file"),
        "unauthorized" => new UnauthorizedAccessException("denied"),
        "invalid" => new InvalidOperationException("bad state"),
        "notimpl" => new NotImplementedException("not yet"),
        "timeout" => new TimeoutException("too slow"),
        _ => new Exception("generic")
    };
}

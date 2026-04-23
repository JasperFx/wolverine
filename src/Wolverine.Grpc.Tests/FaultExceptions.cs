namespace Wolverine.Grpc.Tests;

/// <summary>
///     Shared helper for the exception-mapping integration tests. Given a string kind,
///     returns the matching .NET exception so handlers can be parameterised over the
///     mapping table in <see cref="WolverineGrpcExceptionMapper"/>.
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
        "domain-validation" => new DomainValidationException("invalid domain state"),
        _ => new Exception("generic")
    };
}

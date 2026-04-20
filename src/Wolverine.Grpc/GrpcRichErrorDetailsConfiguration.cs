using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using ProtoMessage = Google.Protobuf.IMessage;
using Status = Google.Rpc.Status;

namespace Wolverine.Grpc;

/// <summary>
///     Ephemeral builder passed to <c>opts.UseGrpcRichErrorDetails(cfg =&gt; ...)</c>. Holds the
///     rich-error-details knobs for a single Wolverine host and decomposes them into
///     <see cref="IServiceCollection"/> registrations when <c>UseGrpcRichErrorDetails</c> returns.
///     Not a long-lived singleton — the resulting DI registrations are authoritative.
/// </summary>
public sealed class GrpcRichErrorDetailsConfiguration
{
    internal List<Action<IServiceCollection>> Registrations { get; } = new();
    internal bool DefaultErrorInfoEnabled { get; private set; }

    /// <summary>
    ///     Register <see cref="DefaultErrorInfoProvider"/> as the last provider in the chain so
    ///     any unmapped exception becomes <see cref="Code.Internal"/> + an opaque
    ///     <see cref="ErrorInfo"/>. Off by default — no stack traces or internal messages are
    ///     ever leaked when this is enabled.
    /// </summary>
    public GrpcRichErrorDetailsConfiguration EnableDefaultErrorInfo()
    {
        DefaultErrorInfoEnabled = true;
        return this;
    }

    /// <summary>
    ///     Inline-map a specific exception type to a <see cref="StatusCode"/> plus caller-supplied
    ///     detail payloads. Shorthand for writing a full <see cref="IGrpcStatusDetailsProvider"/>
    ///     when only one exception type is involved.
    /// </summary>
    /// <param name="code">The gRPC status code emitted when <typeparamref name="TException"/> is thrown.</param>
    /// <param name="details">
    ///     Factory that produces the detail payloads packed into the status. Typically returns one
    ///     or more of AIP-193's standard payloads (<see cref="BadRequest"/>, <see cref="PreconditionFailure"/>,
    ///     etc.).
    /// </param>
    public GrpcRichErrorDetailsConfiguration MapException<TException>(
        StatusCode code,
        Func<TException, ServerCallContext, IEnumerable<ProtoMessage>> details)
        where TException : Exception
    {
        Registrations.Add(services =>
            services.AddSingleton<IGrpcStatusDetailsProvider>(
                _ => new InlineStatusDetailsProvider<TException>(code, details)));
        return this;
    }

    /// <summary>
    ///     Register a reusable <see cref="IGrpcStatusDetailsProvider"/> resolved from the DI
    ///     container. Use this when a provider needs scoped dependencies or is shared across
    ///     multiple exception types.
    /// </summary>
    public GrpcRichErrorDetailsConfiguration AddProvider<TProvider>()
        where TProvider : class, IGrpcStatusDetailsProvider
    {
        Registrations.Add(services =>
            services.AddSingleton<IGrpcStatusDetailsProvider, TProvider>());
        return this;
    }
}

internal sealed class InlineStatusDetailsProvider<TException> : IGrpcStatusDetailsProvider
    where TException : Exception
{
    private readonly StatusCode _code;
    private readonly Func<TException, ServerCallContext, IEnumerable<ProtoMessage>> _details;

    public InlineStatusDetailsProvider(
        StatusCode code,
        Func<TException, ServerCallContext, IEnumerable<ProtoMessage>> details)
    {
        _code = code;
        _details = details;
    }

    public Status? BuildStatus(Exception exception, ServerCallContext context)
    {
        if (exception is not TException typed) return null;

        var status = new Status
        {
            Code = (int)_code,
            Message = typed.Message
        };

        foreach (var detail in _details(typed, context))
        {
            status.Details.Add(Any.Pack(detail));
        }

        return status;
    }
}

internal sealed class WolverineGrpcRichDetailsMarker
{
}

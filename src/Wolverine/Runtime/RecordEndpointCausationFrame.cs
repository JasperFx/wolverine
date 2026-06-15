using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Runtime;

/// <summary>
/// Codegen frame (CritterWatch #396 Phase 4 item 5) that injects an
/// <see cref="EndpointCausation.RecordEndpointCauseAndEffect"/> call into a generated HTTP/gRPC
/// endpoint so messages it publishes are attributed to the endpoint origin (route+verb /
/// service.method). The parallel to <see cref="RecordMessageCausationFrame"/> for endpoints,
/// which are not <see cref="Handlers.MessageHandler"/> subclasses. Applied by Wolverine.Http /
/// the gRPC chain when message-tracking diagnostics are enabled.
/// </summary>
public class RecordEndpointCausationFrame : Frame
{
    private readonly string _endpointOrigin;
    private readonly string _handlerType;
    private Variable? _context;

    public RecordEndpointCausationFrame(string endpointOrigin, string handlerType) : base(false)
    {
        _endpointOrigin = endpointOrigin;
        _handlerType = handlerType;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(EndpointCausation).FullName}.{nameof(EndpointCausation.RecordEndpointCauseAndEffect)}(" +
            $"{_context!.Usage}, {_context!.Usage}.Runtime.Observer, \"{_endpointOrigin}\", \"{_handlerType}\");");
        Next?.GenerateCode(method, writer);
    }
}

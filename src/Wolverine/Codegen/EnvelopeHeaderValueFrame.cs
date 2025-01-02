using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Codegen;

internal class EnvelopeHeaderValueFrame : SyncFrame
{
    private Variable _envelope;

    public EnvelopeHeaderValueFrame(string headerName, Type headerType)
    {
        HeaderName = headerName;
        
        // TODO -- this will need to be sanitized
        Header = new Variable(headerType, headerName, this);
    }

    public string HeaderName { get; }

    public Variable Header { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        throw new NotImplementedException();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _envelope = chain.FindVariable(typeof(Envelope));
        yield return _envelope;
    }
}
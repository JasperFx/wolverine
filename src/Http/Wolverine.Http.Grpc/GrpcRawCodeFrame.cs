using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Frame that writes raw code for gRPC method implementations.
/// </summary>
internal class GrpcRawCodeFrame : SyncFrame
{
    private readonly string _code;

    public GrpcRawCodeFrame(string code)
    {
        _code = code;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var lines = _code.Split('\n');
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                writer.WriteLine(line.TrimEnd());
            }
            else if (line.Length == 0)
            {
                writer.BlankLine();
            }
        }
    }
}

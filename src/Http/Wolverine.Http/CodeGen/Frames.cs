using System.Text;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.CodeGen;

public class WriteContentType : SyncFrame
{
    private readonly string _contentType;

    public WriteContentType(string contentType)
    {
        _contentType = contentType;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{EndpointGraph.Context}.{nameof(HttpContext.Response)}.{nameof(HttpResponse.ContentType)} = \"{_contentType}\";");
        Next?.GenerateCode(method, writer);
    }
}

public class WriteContentLength : SyncFrame
{
    private readonly Variable _stringVariable;

    public WriteContentLength(Variable stringVariable)
    {
        _stringVariable = stringVariable;
        uses.Add(_stringVariable);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{EndpointGraph.Context}.{nameof(HttpContext.Response)}.{nameof(HttpResponse.ContentLength)} = {_stringVariable.Usage}.Length;");
        Next?.GenerateCode(method, writer);
    }
}
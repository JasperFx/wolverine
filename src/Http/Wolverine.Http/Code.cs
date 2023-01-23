using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http;


public class EndpointGraph : ICodeFileCollection
{
    public IReadOnlyList<ICodeFile> BuildFiles()
    {
        return new List<ICodeFile>();
    }

    public string ChildNamespace { get; } = "Wolverine.Endpoints";
    public GenerationRules Rules { get; } = new GenerationRules();
}

public abstract class EndpointHandler
{
    public abstract Task Handle(HttpContext context);
}
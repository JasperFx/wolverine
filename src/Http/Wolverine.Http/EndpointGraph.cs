using System.Text.Json;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Lamar;

namespace Wolverine.Http;

public class EndpointGraph : ICodeFileCollection
{
    private readonly WolverineOptions _options;
    public static readonly string Context = "httpContext";

    private readonly List<IResourceWriterPolicy> _writerPolicies = new()
    {
        new StringResourceWriterPolicy(),
        new JsonResourceWriterPolicy()
    };

    public EndpointGraph(WolverineOptions options, IContainer container)
    {
        _options = options;
        Container = container;
    }

    internal IContainer Container { get; } 

    internal IEnumerable<IResourceWriterPolicy> WriterPolicies => _writerPolicies;

    public IReadOnlyList<ICodeFile> BuildFiles()
    {
        return new List<ICodeFile>();
    }

    public string ChildNamespace => "Endpoints";
    public GenerationRules Rules { get; } = new();
}
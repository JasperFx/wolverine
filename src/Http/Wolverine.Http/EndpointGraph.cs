using System.Text.Json;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Lamar;

namespace Wolverine.Http;

public class EndpointGraph : ICodeFileCollection
{
    public static readonly string Context = "httpContext";

    private readonly List<IResourceWriterPolicy> _writerPolicies = new()
    {
        new StringResourceWriterPolicy(),
        new JsonResourceWriterPolicy()
    };

    public EndpointGraph()
    {
        Rules.ReferenceAssembly(GetType().Assembly);
    }

    // TODO -- resolve this from somewhere else!
    internal IContainer Container { get; } = new Container(x =>
    {
        // TODO -- pull the JsonSerializerOptions from Minimal API location
        x.ForConcreteType<JsonSerializerOptions>().Configure.Singleton();
        x.For<IServiceVariableSource>().Use(c => c.CreateServiceVariableSource()).Singleton();
    });

    internal IEnumerable<IResourceWriterPolicy> WriterPolicies => _writerPolicies;

    public IReadOnlyList<ICodeFile> BuildFiles()
    {
        return new List<ICodeFile>();
    }

    public string ChildNamespace => "Endpoints";
    public GenerationRules Rules { get; } = new();
}
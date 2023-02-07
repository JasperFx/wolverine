using JasperFx.CodeGeneration;

namespace Wolverine.Configuration;

/// <summary>
/// This helps track code generated files from extensions to Wolverine
/// </summary>
public class WolverineSupplementalCodeFiles : ICodeFileCollection
{
    private readonly WolverineOptions _options;

    public WolverineSupplementalCodeFiles(WolverineOptions options)
    {
        _options = options;
    }

    public List<ICodeFileCollection> Collections { get; } = new();

    public IReadOnlyList<ICodeFile> BuildFiles()
    {
        return Collections.SelectMany(x => x.BuildFiles()).ToList();
    }

    public string ChildNamespace => "Wolverine";
    public GenerationRules Rules => _options.Node.CodeGeneration;
}
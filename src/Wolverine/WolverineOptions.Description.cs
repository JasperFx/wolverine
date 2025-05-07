using JasperFx.CommandLine;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core.Descriptors;
using JasperFx.Core.Reflection;
using Spectre.Console;
using Wolverine.Runtime.Serialization;
using Table = Spectre.Console.Table;
using Tree = Spectre.Console.Tree;

namespace Wolverine;

public partial class WolverineOptions : IDescribeMyself
{

    internal void WriteToConsole()
    {
        AnsiConsole.Write("Wolverine Options");
        
        AnsiConsole.WriteLine();

        var root = new Tree(new Markup($"[bold]Service Name: {ServiceName.EscapeMarkup()}[/]"));

        root.AddNode(nameof(DefaultExecutionTimeout)).AddNode(DefaultExecutionTimeout.ToString());
        root.AddNode(nameof(AutoBuildMessageStorageOnStartup)).AddNode(AutoBuildMessageStorageOnStartup.ToString());
        root.AddNode(nameof(CodeGeneration.TypeLoadMode)).AddNode(CodeGeneration.TypeLoadMode.ToString());
        root.AddNode(nameof(ExternalTransportsAreStubbed)).AddNode(ExternalTransportsAreStubbed.ToString());
        
        writeAssemblies(root);
        writeExtensions(root);
        writeSerializers(root);
        
        AnsiConsole.Write(root);

        // TODO -- make all this fancier
        foreach (var part in Parts)
        {
            OptionDescriptionWriter.Write(part.ToDescription());
        }
    }

    internal List<IDescribeMyself> Parts { get; } = new();

    private void writeSerializers(Tree root)
    {
        var table = new Table();
        table.AddColumns("Content Type", "Serializer");
        foreach (var pair in _serializers)
        {
            var contentType = pair.Key;
            var serializer = pair.Value.GetType().ShortNameInCode();
            if (pair.Value == _defaultSerializer)
            {
                serializer += " (default)";
            }

            table.AddRow(contentType, serializer);
        }

        root.AddNode("Serializers").AddNode(table);
    }

    private void writeExtensions(Tree root)
    {
        var tree = root.AddNode("Extensions");
        if (_extensionTypes.Any())
        {
            foreach (var extensionType in _extensionTypes) tree.AddNode(extensionType.FullNameInCode());
        }
        else
        {
            tree.AddNode(new Markup("[gray]No applied extensions.[/]"));
        }
    }

    private void writeAssemblies(Tree root)
    {
        var tree = root.AddNode("Assemblies");
        tree.AddNode(ApplicationAssembly!.GetName().Name + " (application)");

        var assemblies = Assemblies
            .Where(x => x != ApplicationAssembly)
            .Select(x => x.GetName().Name)
            .OrderBy(x => x);
        foreach (var assembly in assemblies) tree.AddNode(assembly!);
    }

    internal Dictionary<string, IMessageSerializer> ToSerializerDictionary()
    {
        return _serializers.ToDictionary(x => x.Key, x => x.Value);
    }

    public OptionsDescription ToDescription()
    {
        var description = new OptionsDescription(this);
        description.AddValue("Version", GetType().Assembly.GetName().Version?.ToString());

        description.AddChildSet("Transports", Transports);
        description.AddChildSet("Endpoints", Transports.SelectMany(x => x.Endpoints()));
        description.AddChildSet("Handlers", HandlerGraph.AllChains());
        
        // TODO -- add application assembly
        // TODO -- add handler assemblies
        // TODO -- add handlers, correlated to sticky endpoints as necessary
        // TODO -- add transports
        // TODO -- add endpoints underneath each transport

        return description;
    }
}
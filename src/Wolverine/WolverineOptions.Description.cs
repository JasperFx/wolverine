using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Lamar.IoC.Diagnostics;
using Oakton.Descriptions;
using Spectre.Console;

namespace Wolverine;

public partial class WolverineOptions : IDescribedSystemPart, IWriteToConsole
{
    async Task IDescribedSystemPart.Write(TextWriter writer)
    {
        foreach (var transport in Transports.Where(x => x.Endpoints().Any()))
        {
            await writer.WriteLineAsync(transport.Name);

            foreach (var endpoint in transport.Endpoints())
            {
                await writer.WriteLineAsync(
                    $"{endpoint.Uri}, Incoming: {endpoint.IsListener}, Reply Uri: {endpoint.IsUsedForReplies}");
            }

            await writer.WriteLineAsync();
        }
    }

    string IDescribedSystemPart.Title => "Wolverine Options";

    Task IWriteToConsole.WriteToConsole()
    {
        var root = new Tree(new Markup($"[bold]Service Name: {ServiceName.EscapeMarkup()}[/]"));
        
        writeAssemblies(root);
        writeExtensions(root);
        writeSerializers(root);

        root.AddNode(nameof(DefaultExecutionTimeout)).AddNode(DefaultExecutionTimeout.ToString());
        root.AddNode(nameof(AutoBuildEnvelopeStorageOnStartup)).AddNode(AutoBuildEnvelopeStorageOnStartup.ToString());
        root.AddNode(nameof(CodeGeneration.TypeLoadMode)).AddNode(CodeGeneration.TypeLoadMode.ToString());
        root.AddNode(nameof(ExternalTransportsAreStubbed)).AddNode(ExternalTransportsAreStubbed.ToString());

        AnsiConsole.Write(root);

        return Task.CompletedTask;
    }

    private void writeSerializers(Tree root)
    {
        var table = new Table();
        table.AddColumns("Content Type", "Serializer");
        foreach (var pair in _serializers)
        {
            var contentType = pair.Key;
            var serializer = pair.Value.GetType().FullNameInCode();
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
            foreach (var extensionType in _extensionTypes)
            {
                tree.AddNode(extensionType.FullNameInCode());
            }
        }
        else
        {
            tree.AddNode(new Markup("[gray]No applied extensions.[/]"));
        }
        
    }

    private void writeAssemblies(Tree root)
    {
        var tree = root.AddNode("Assemblies");
        tree.AddNode(ApplicationAssembly.GetName().Name + " (application)");

        var assemblies = Assemblies
            .Where(x => x != ApplicationAssembly)
            .Select(x => x.GetName().Name)
            .OrderBy(x => x);
        foreach (var assembly in assemblies)
        {
            tree.AddNode(assembly);
        }

    }
}
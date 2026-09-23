using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;

namespace Wolverine.Configuration;

/// <summary>
/// GH-4527. Thrown once, at the top of Wolverine's startup, naming <b>every</b> connection string the
/// application asked for by name and could not find -- rather than one at a time from inside whichever DI
/// factory happened to be resolved first.
/// </summary>
public class MissingNamedConnectionStringsException : Exception
{
    public MissingNamedConnectionStringsException(IReadOnlyList<NamedConfigurationDependency> missing,
        IReadOnlyList<string> configuredNames)
        : base(buildMessage(missing, configuredNames))
    {
        Missing = missing;
        ConfiguredNames = configuredNames;
    }

    /// <summary>
    /// Every dependency whose connection string could not be resolved, not just the first.
    /// </summary>
    public IReadOnlyList<NamedConfigurationDependency> Missing { get; }

    /// <summary>
    /// The connection string <b>keys</b> that are configured. Names only -- never the values, which are
    /// credentials.
    /// </summary>
    public IReadOnlyList<string> ConfiguredNames { get; }

    private static string buildMessage(IReadOnlyList<NamedConfigurationDependency> missing,
        IReadOnlyList<string> configuredNames)
    {
        var writer = new StringBuilder();

        if (missing.Count == 1)
        {
            var only = missing[0];
            writer.Append(
                $"The connection string named '{only.Name}' required by the {only.Kind} transport is missing from configuration.");
        }
        else
        {
            writer.AppendLine($"{missing.Count} connection strings required by this Wolverine application are missing from configuration:");
            foreach (var dependency in missing)
            {
                writer.AppendLine($"  * '{dependency.Name}', required by the {dependency.Kind} transport");
            }
        }

        writer.Append(configuredNames.Any()
            ? $" Configured connection strings: {configuredNames.Select(x => $"'{x}'").Join(", ")}."
            : " No connection strings are configured at all.");

        // The Aspire wiring is the dominant source of this failure -- a resource that was never added in the
        // AppHost, or added but never referenced from this project -- so name that remedy explicitly rather
        // than leaving the user to work out why a green build cannot find its broker.
        var aspireExamples = missing
            .Where(x => x.AspireResourceMethod.IsNotEmpty())
            .Select(x => $"builder.{x.AspireResourceMethod}(\"{x.Name}\")")
            .Distinct()
            .ToArray();

        if (aspireExamples.Any())
        {
            writer.Append(
                $" If this application runs under .NET Aspire, add the resource in the AppHost ({aspireExamples.Join("; ")}) and reference it from this project with .WithReference(...).");
        }

        writer.Append(
            $" Otherwise add {missing.Select(x => $"ConnectionStrings:{x.Name}").Join(" and ")} to appsettings.json, user secrets, or the environment.");

        return writer.ToString();
    }
}

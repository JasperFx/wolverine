using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using JasperFx.CodeGeneration;
using JasperFx.CommandLine;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Configuration.EventModeling.Scaffolding;

public class ScaffoldInput : NetCoreInput
{
    [Description("Directory the skeletons are written under, one folder per domain; defaults to the current directory")]
    [FlagAlias("output", 'o')]
    public string? OutputFlag { get; set; }

    [Description("Root namespace of the scaffolded code; defaults to the application assembly's name. A slice with a domain goes in <root>.<Domain>")]
    [FlagAlias("namespace", 'n')]
    public string? NamespaceFlag { get; set; }

    [Description("Which of the application's Event Models to scaffold, when it hosts more than one")]
    public string? NameFlag { get; set; }

    [Description("Report what would be written without writing anything")]
    [FlagAlias("dry-run", 'd')]
    public bool DryRunFlag { get; set; }
}

/// <summary>
///     <c>dotnet run -- scaffold [--output &lt;dir&gt;] [--namespace &lt;root&gt;] [--dry-run]</c>: write an
///     implementation skeleton for every Event Model slice that is declared but not yet implemented
///     (GH-4832). The host is built but never started, exactly as for <see cref="EventModelCommand" />, so the
///     declared model and Wolverine's derived chains are assembled and the slices with no code behind them
///     found — which only the application itself can answer.
/// </summary>
/// <remarks>
///     The report is one line per slice, aggregate or view, written so an agent can act on it: <c>WROTE</c>
///     and <c>EXISTS</c> name the file; <c>EDIT</c> names an existing aggregate or view stub, the file it is
///     declared in, and the exact <c>Apply</c> methods to add to it; <c>UNKNOWN</c> names a slice whose trigger
///     neither the declaration nor the code reveals. See <see cref="SliceScaffolder" /> for the rules.
/// </remarks>
[Description("Write implementation skeletons for the Event Model slices that are declared but have no code yet",
    Name = "scaffold")]
public class ScaffoldCommand : JasperFxAsyncCommand<ScaffoldInput>
{
    public ScaffoldCommand()
    {
        Usage("Scaffold every declared-only slice into the current directory");
    }

    public override async Task<bool> Execute(ScaffoldInput input)
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = input.BuildHost();

            // Not started: resolving the code file collections compiles the handler graph, as event-model does
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var set = await WolverineEventModelExport.AssembleSetAsync(host.Services);

            EventModelDescriptor model;
            if (input.NameFlag.IsNotEmpty())
            {
                if (set.Find(input.NameFlag!) is not { } selected)
                {
                    Console.WriteLine($"This application hosts no Event Model named '{input.NameFlag}'. It hosts {string.Join(", ", set.Models.Select(x => $"'{x.Name}'"))}.");
                    return false;
                }

                model = selected;
            }
            else if (set.Sole is { } sole)
            {
                model = sole;
            }
            else if (set.Models.Count == 0)
            {
                Console.WriteLine("This application hosts no Event Model, so there is nothing to scaffold.");
                return true;
            }
            else
            {
                Console.WriteLine($"This application hosts {set.Models.Count} Event Models ({string.Join(", ", set.Models.Select(x => $"'{x.Name}'"))}). Pass --name to scaffold one of them.");
                return false;
            }

            var options = host.Services.GetRequiredService<WolverineOptions>();
            var output = (input.OutputFlag.IsNotEmpty() ? input.OutputFlag! : Directory.GetCurrentDirectory()).ToFullPath();

            var plan = SliceScaffolder.Plan(model, new SliceScaffoldOptions
            {
                RootNamespace = input.NamespaceFlag.IsNotEmpty()
                    ? input.NamespaceFlag!
                    : options.ApplicationAssembly?.GetName().Name ?? "App",
                ResolveType = TypeResolver.For(options, model),
                FileExists = path => File.Exists(Path.Combine(output, path)),
                FindSourceFile = SourceFiles.Finder(Directory.GetCurrentDirectory())
            });

            if (!input.DryRunFlag)
            {
                foreach (var file in plan.Files)
                {
                    if (file.AppendClass is not null)
                    {
                        // GH-4891: the handler goes into the file that declares its command. The source
                        // finder reports that file relative to the current directory.
                        var target = Path.Combine(Directory.GetCurrentDirectory(), file.RelativePath);
                        var appended = SliceScaffolder.AppendTo(await File.ReadAllTextAsync(target), file);
                        if (appended is null)
                        {
                            Console.WriteLine($"EXISTS {file.RelativePath} -- {file.AppendClass} is already declared there; nothing appended.");
                            continue;
                        }

                        await File.WriteAllTextAsync(target, appended);
                        continue;
                    }

                    var path = Path.Combine(output, file.RelativePath);
                    if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);

                    // CreateNew: a file that appeared since the plan was made is still never overwritten
                    await using var stream = new FileStream(path, FileMode.CreateNew);
                    await using var writer = new StreamWriter(stream);
                    await writer.WriteAsync(file.Code);
                }
            }

            var declaredOnly = model.Slices.Count(SliceScaffolder.IsDeclaredOnly);
            Console.WriteLine(declaredOnly == 0
                ? $"Every slice of the Event Model '{model.Name}' already has code behind it; nothing to scaffold."
                : $"The Event Model '{model.Name}' has {declaredOnly} declared slice(s) with no code yet. Output: {output}{(input.DryRunFlag ? " (dry run, nothing written)" : "")}");

            foreach (var notice in plan.Notices) Console.WriteLine(notice);

            return true;
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    /// <summary>Find the CLR type behind a descriptor; a name-only declaration matches a type of that name in the application.</summary>
    internal static class TypeResolver
    {
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "CLI scaffold path; never dispatch.")]
        [UnconditionalSuppressMessage("Trimming", "IL2057", Justification = "CLI scaffold path; never dispatch.")]
        public static Func<TypeDescriptor, Type?> For(WolverineOptions options, EventModelDescriptor model)
        {
            // The application assembly, and every assembly a real type in the model comes from
            var assemblies = new List<Assembly>();
            if (options.ApplicationAssembly is { } app) assemblies.Add(app);

            foreach (var name in model.Slices.SelectMany(typesOf).Select(x => x.AssemblyName)
                         .Where(x => x.IsNotEmpty()).Distinct())
            {
                try
                {
                    var assembly = Assembly.Load(name);
                    if (!assemblies.Contains(assembly)) assemblies.Add(assembly);
                }
                catch (Exception)
                {
                    // not loadable here: its types stay unresolved
                }
            }

            Type[] typesIn(Assembly assembly)
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    return e.Types.Where(x => x is not null).ToArray()!;
                }
            }

            var byName = assemblies.SelectMany(typesIn)
                .Where(x => !x.IsNested && !x.IsGenericTypeDefinition)
                .GroupBy(x => x.Name, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);

            return descriptor =>
            {
                if (descriptor.AssemblyName.IsNotEmpty())
                {
                    try
                    {
                        if (Type.GetType($"{descriptor.FullName}, {descriptor.AssemblyName}", false) is { } type) return type;
                    }
                    catch (Exception)
                    {
                        // fall back to the name
                    }
                }

                return byName.TryGetValue(descriptor.Name, out var candidates) && candidates.Length == 1
                    ? candidates[0]
                    : null;
            };
        }

        private static IEnumerable<TypeDescriptor> typesOf(EventModelSliceDescriptor slice)
        {
            if (slice.CommandType is { } command) yield return command;
            if (slice.StartsStream is { } stream) yield return stream;
            foreach (var type in slice.AggregateTypes.Concat(slice.EmittedEvents).Concat(slice.PublishedMessages)
                         .Concat(slice.ReadModelTypes).Concat(slice.ConsumedEvents).Concat(slice.ReadsFrom))
            {
                yield return type;
            }
        }
    }

    /// <summary>Find the source file that declares a type, below the solution (or project) directory.</summary>
    internal static class SourceFiles
    {
        public static Func<Type, string?> Finder(string startingDirectory)
        {
            var root = SolutionRoot(startingDirectory);
            string[]? files = null;

            return type =>
            {
                files ??= sourceFilesUnder(root).ToArray();

                var declaration = new Regex($@"\b(class|record|struct|interface)\s+(class\s+|struct\s+)?{Regex.Escape(type.Name)}\b");

                bool declares(string file)
                {
                    try
                    {
                        return declaration.IsMatch(File.ReadAllText(file));
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                }

                // The conventional file name first; only then read every file
                var named = files.Where(x => Path.GetFileNameWithoutExtension(x) == type.Name).Where(declares).ToArray();
                var matches = named.Length > 0 ? named : files.Where(declares).ToArray();

                // Prefer the file that also declares the type's namespace
                var chosen = matches.Length <= 1
                    ? matches.FirstOrDefault()
                    : matches.FirstOrDefault(x => type.Namespace is { } ns && File.ReadAllText(x).Contains($"namespace {ns}")) ?? matches[0];

                return chosen is null ? null : Path.GetRelativePath(startingDirectory, chosen);
            };
        }

        private static IEnumerable<string> sourceFilesUnder(string directory)
        {
            var pending = new Stack<string>();
            pending.Push(directory);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                string[] files, directories;
                try
                {
                    files = Directory.GetFiles(current, "*.cs");
                    directories = Directory.GetDirectories(current);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var file in files) yield return file;

                foreach (var child in directories)
                {
                    var name = Path.GetFileName(child);
                    if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules") continue;
                    pending.Push(child);
                }
            }
        }

        internal static string SolutionRoot(string directory)
        {
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            {
                // GH-4885: in a git worktree (or a submodule) .git is a *file* pointing at the real
                // repository, so look for either. Missing it walked on up into the parent tree and
                // scanned -- and matched types in -- every checkout beside this one.
                if (current.EnumerateFiles("*.sln").Any() || current.EnumerateFiles("*.slnx").Any() ||
                    current.EnumerateFileSystemInfos(".git").Any())
                {
                    return current.FullName;
                }
            }

            return directory;
        }
    }
}

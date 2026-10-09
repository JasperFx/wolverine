using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Spectre.Console;

namespace Wolverine.Configuration.EventModeling;

/// <summary>What <see cref="EventModelReport" /> shows of a model.</summary>
/// <param name="Chapter">Only slices in this chapter.</param>
/// <param name="Domain">Only slices in this domain.</param>
/// <param name="HotspotsOnly">Only slices with an open question on them.</param>
public sealed record EventModelReportFilter(string? Chapter = null, string? Domain = null, bool HotspotsOnly = false);

/// <summary>
///     GH-4917. An assembled Event Model as a person reads it: domain, then chapter, then each slice with its
///     trigger, command, aggregate and why, the events it emits, what it publishes, reads and produces, the
///     code behind it, its specifications and its hotspots. As a Spectre.Console tree in a terminal, and as
///     plain indented text — no tree glyphs, no colour — to paste into an issue or a pull request.
/// </summary>
public static class EventModelReport
{
    private const string NoDomain = "(no domain)";
    private const string NoChapter = "(no chapter)";

    /// <summary>One slice, as the lines the report shows for it.</summary>
    public sealed record SliceReport(string Domain, string Chapter, string Heading, IReadOnlyList<string> Facts,
        IReadOnlyList<string> Hotspots, bool Implemented, bool Specified);

    public static IReadOnlyList<SliceReport> Slices(EventModelDescriptor model, EventModelReportFilter? filter = null)
    {
        filter ??= new EventModelReportFilter();
        return model.Slices
            .Where(x => filter.Chapter is null || string.Equals(x.Chapter, filter.Chapter, StringComparison.OrdinalIgnoreCase))
            .Where(x => filter.Domain is null || string.Equals(x.Domain, filter.Domain, StringComparison.OrdinalIgnoreCase))
            .Where(x => !filter.HotspotsOnly || x.Hotspots.Count > 0)
            .Select(describe)
            .OrderBy(x => x.Domain == NoDomain).ThenBy(x => x.Domain, StringComparer.Ordinal)
            .ThenBy(x => x.Chapter == NoChapter).ThenBy(x => x.Chapter, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The model as plain indented text, for a terminal that is not one.</summary>
    public static string Plain(EventModelDescriptor model, EventModelReportFilter? filter = null)
    {
        var slices = Slices(model, filter);
        var writer = new StringWriter();
        writer.WriteLine($"Event Model '{model.Name}': {slices.Count} slice(s)");

        foreach (var domain in slices.GroupBy(x => x.Domain))
        {
            writer.WriteLine();
            writer.WriteLine(domain.Key);
            foreach (var chapter in domain.GroupBy(x => x.Chapter))
            {
                writer.WriteLine($"  {chapter.Key}");
                foreach (var slice in chapter)
                {
                    writer.WriteLine($"    {slice.Heading}");
                    foreach (var fact in slice.Facts) writer.WriteLine($"      {fact}");
                    foreach (var hotspot in slice.Hotspots) writer.WriteLine($"      ! {hotspot}");
                }
            }
        }

        foreach (var hotspot in model.Hotspots)
        {
            writer.WriteLine();
            writer.WriteLine($"! {hotspot.Origin}: {hotspot.Text}");
        }

        writer.WriteLine();
        foreach (var line in summary(slices)) writer.WriteLine(line);
        return writer.ToString();
    }

    /// <summary>The model as a Spectre.Console tree and a summary table, for a terminal.</summary>
    public static void Write(IAnsiConsole console, EventModelDescriptor model, EventModelReportFilter? filter = null)
    {
        var slices = Slices(model, filter);
        var tree = new Tree($"[bold]Event Model '{Markup.Escape(model.Name)}'[/]: {slices.Count} slice(s)");

        foreach (var domain in slices.GroupBy(x => x.Domain))
        {
            var domainNode = tree.AddNode($"[bold blue]{Markup.Escape(domain.Key)}[/]");
            foreach (var chapter in domain.GroupBy(x => x.Chapter))
            {
                var chapterNode = domainNode.AddNode($"[bold]{Markup.Escape(chapter.Key)}[/]");
                foreach (var slice in chapter)
                {
                    var colour = slice.Hotspots.Count > 0 ? "yellow" : slice.Implemented ? "green" : "grey";
                    var node = chapterNode.AddNode($"[{colour}]{Markup.Escape(slice.Heading)}[/]");
                    foreach (var fact in slice.Facts) node.AddNode(Markup.Escape(fact));
                    foreach (var hotspot in slice.Hotspots) node.AddNode($"[yellow]⚠ {Markup.Escape(hotspot)}[/]");
                }
            }
        }

        foreach (var hotspot in model.Hotspots)
        {
            tree.AddNode($"[yellow]⚠ {Markup.Escape(hotspot.Origin.ToString())}: {Markup.Escape(hotspot.Text)}[/]");
        }

        console.Write(tree);
        console.WriteLine();

        var table = new Table().AddColumns("Chapter", "Slices", "Implemented", "Specified", "Hotspots");
        foreach (var chapter in slices.GroupBy(x => x.Chapter))
        {
            table.AddRow(Markup.Escape(chapter.Key), chapter.Count().ToString(), chapter.Count(x => x.Implemented).ToString(),
                chapter.Count(x => x.Specified).ToString(), chapter.Sum(x => x.Hotspots.Count).ToString());
        }

        console.Write(table);
    }

    private static IEnumerable<string> summary(IReadOnlyList<SliceReport> slices)
    {
        yield return "Chapter | Slices | Implemented | Specified | Hotspots";
        foreach (var chapter in slices.GroupBy(x => x.Chapter))
        {
            yield return $"{chapter.Key} | {chapter.Count()} | {chapter.Count(x => x.Implemented)} | {chapter.Count(x => x.Specified)} | {chapter.Sum(x => x.Hotspots.Count)}";
        }
    }

    private static SliceReport describe(EventModelSliceDescriptor slice)
    {
        var heading = slice.Name;
        if (slice.Pattern is { } pattern) heading += $"  [{pattern}]";
        var trigger = string.Join(" ", new[] { slice.TriggerKind?.ToString(), slice.TriggerLabel is { Length: > 0 } label ? $"\"{label}\"" : null }
            .OfType<string>());
        if (trigger.Length > 0) heading += $"  <- {trigger}";
        if (slice.Provenance is { } provenance) heading += $"  ({provenance.ToString().ToLowerInvariant()})";

        var facts = new List<string>();
        if (slice.CommandType is { } command) facts.Add($"command: {command.Name}");
        if (slice.StartsStream is { } started) facts.Add($"starts: the {started.Name} stream");

        var aggregates = names(slice.AggregateTypes);
        switch (slice.AggregateDeclaration)
        {
            case AggregateDeclaration.Default:
                facts.Add($"aggregate: {aggregates} (the chapter's ForAggregate default)");
                break;
            case AggregateDeclaration.None:
                facts.Add("aggregate: none, declared with NoAggregate()");
                break;
            case AggregateDeclaration.DeciderModel:
                facts.Add($"decider: {slice.DeciderModel?.Name ?? "?"} (DCB)");
                break;
            default:
                if (slice.AggregateTypes.Count > 0) facts.Add($"aggregate: {aggregates}");
                break;
        }

        if (slice.ConsumedEvents.Count > 0) facts.Add($"on: {names(slice.ConsumedEvents)}");
        if (slice.EmittedEvents.Count > 0) facts.Add($"emits: {names(slice.EmittedEvents)}");
        if (slice.PublishedMessages.Count > 0) facts.Add($"publishes: {names(slice.PublishedMessages)}");
        if (slice.ReadsFrom.Count > 0) facts.Add($"reads: {names(slice.ReadsFrom)}");
        if (slice.ReadModelTypes.Count > 0) facts.Add($"produces: {names(slice.ReadModelTypes)}");
        foreach (var external in slice.ExternalSystems) facts.Add($"external: {external.Name} ({external.Direction})");

        facts.Add(slice.HandlerType is { } handler ? $"handled by: {handler.Name}" : "handled by: nothing yet");
        foreach (var spec in slice.Specifications) facts.Add($"spec: {spec.Identity}");

        var hotspots = slice.Hotspots.Select(x => $"{x.Origin}: {x.Text}").ToList();
        return new SliceReport(slice.Domain is { Length: > 0 } domain ? domain : NoDomain,
            slice.Chapter is { Length: > 0 } chapter ? chapter : NoChapter, heading, facts, hotspots,
            slice.HandlerType is not null, slice.Specifications.Count > 0);
    }

    private static string names(IReadOnlyList<TypeDescriptor> types) => string.Join(", ", types.Select(x => x.Name));
}

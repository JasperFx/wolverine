using System.Reflection;
using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;

namespace Wolverine.Bobcat.EventModel;

/// <summary>
/// TEMPORARY (GH-5082): the four members of Bobcat.EventModel the spec writer used while it lived in that assembly
/// and which are <c>internal</c> there. Each is reached here, and only here, until Bobcat.EventModel makes them
/// public (or grants <c>InternalsVisibleTo("Wolverine.Bobcat.EventModel")</c>); then this file is deleted and the
/// call sites name the members directly.
/// <list type="bullet">
/// <item><c>CuratedScenario.Source</c> and <c>CuratedScenario.SourceChapter</c>: the board example (and its chapter)
/// a model scenario came from. Not derivable from public data: the import attaches each example to a slice by its
/// command or view and keeps the first of two equal titles, so matching titles back would bind "happy path" to the
/// wrong slice's example.</item>
/// <item><c>CuratedFieldTypes.TryInfer</c>: the type a sample value implies. Reached rather than copied, so a
/// member the spec writer adds to a stub is typed exactly as the stub writer would type it.</item>
/// <item><c>EmlangReader.Text</c>: a board value as text. Trivial, so copied.</item>
/// </list>
/// </summary>
internal static class EventModelInternals
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly PropertyInfo source = typeof(CuratedScenario).GetProperty("Source", Instance)
        ?? throw missing("CuratedScenario.Source");

    private static readonly PropertyInfo sourceChapter = typeof(CuratedScenario).GetProperty("SourceChapter", Instance)
        ?? throw missing("CuratedScenario.SourceChapter");

    private delegate bool TryInferType(string sketch, out string type);

    private static readonly TryInferType tryInfer = (TryInferType)Delegate.CreateDelegate(typeof(TryInferType),
        typeof(CSharpModelWriter).Assembly.GetType("Bobcat.EventModel.CuratedFieldTypes")
            ?.GetMethod("TryInfer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw missing("CuratedFieldTypes.TryInfer"));

    /// <summary>The board example a scenario was imported from, or null for one the import did not write from a board.</summary>
    public static EmlangTest? SourceOf(this CuratedScenario scenario) => (EmlangTest?)source.GetValue(scenario);

    /// <summary>The chapter <see cref="SourceOf"/> was written under.</summary>
    public static EmlangChapter? SourceChapterOf(this CuratedScenario scenario) => (EmlangChapter?)sourceChapter.GetValue(scenario);

    /// <summary>Bobcat.EventModel's <c>CuratedFieldTypes.TryInfer</c>.</summary>
    public static bool TryInferFieldType(string sketch, out string type) => tryInfer(sketch, out type);

    /// <summary>A copy of Bobcat.EventModel's <c>EmlangReader.Text</c>: a board value as text.</summary>
    public static string Text(object? value) => value switch
    {
        null => string.Empty,
        List<object> items => string.Join(", ", items.Select(Text)),
        Dictionary<object, object> map => string.Join(", ", map.Select(x => $"{x.Key}: {Text(x.Value)}")),
        _ => value.ToString() ?? string.Empty
    };

    private static InvalidOperationException missing(string member)
        => new($"This Bobcat.EventModel has no {member}, which WolverineFx.Bobcat.EventModel's spec writer reads (GH-5082). Use the Bobcat.EventModel version WolverineFx.Bobcat.EventModel was built against.");
}

/// <summary>Bobcat.EventModel's <c>CuratedFieldTypes</c>, as far as the spec writer uses it.</summary>
internal static class CuratedFieldTypes
{
    /// <summary>The same constant as Bobcat.EventModel's: the stub type of a list of strings.</summary>
    public const string StringList = "List<string>";

    public static bool TryInfer(string sketch, out string type) => EventModelInternals.TryInferFieldType(sketch, out type);
}

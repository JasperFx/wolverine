using Bobcat;
using Bobcat.Engine;

namespace Wolverine.Bobcat;

/// <summary>
/// A Then step's assertion did not hold, and no Bobcat scenario was recording to gather it.
/// </summary>
public sealed class SpecificationFailedException(string message) : Exception(message);

/// <summary>
/// How every helper in this library reports a verdict, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Inside a scenario</b> — a test in a <c>[BobcatFeature]</c> class, or one carrying <c>[BobcatSpec]</c> — a wrong
/// is recorded on the step in progress through <see cref="SpecAssert" /> and the test carries on, so
/// the specification shows every disagreement rather than only its first. The runner settles the
/// verdict at the end.
/// </para>
/// <para>
/// <b>Outside one</b> — a plain test, or a Gherkin fixture, neither of which opens a
/// <see cref="ScenarioRecorder" /> recording — <see cref="SpecAssert" /> is silent by design, which
/// here would turn a red test green. So the wrong is thrown instead.
/// </para>
/// </remarks>
internal static class Verdicts
{
    public static bool Recording => ScenarioRecorder.Current is not null;

    /// <summary>Report a wrong. Returns false, so a step can <c>return Verdicts.Fail(...)</c>.</summary>
    public static bool Fail(string message)
    {
        if (!Recording) throw new SpecificationFailedException(message);

        SpecAssert.Fail(message);
        return false;
    }

    /// <summary>Report <paramref name="because" /> unless <paramref name="condition" /> holds.</summary>
    public static bool Fact(bool condition, string because) => condition || Fail(because);

    /// <summary>A judged cell: expected against actual, recorded on the step in progress.</summary>
    public static bool Check(string name, object? actual, object? expected, int rowIndex = -1)
    {
        var matched = ObjectComparison.LeafEquals(actual, expected);
        Cell(name, matched, expected, actual, rowIndex);
        return matched;
    }

    /// <summary>Record one judged cell, whatever decided it.</summary>
    public static void Cell(string name, bool matched, object? expected, object? actual, int rowIndex = -1)
    {
        if (!Recording) return;

        ScenarioRecorder.RecordCell(new CellResult(name, matched ? ResultStatus.success : ResultStatus.failed)
        {
            Expected = ObjectComparison.Format(expected),
            Actual = ObjectComparison.Format(actual),
            RowIndex = rowIndex
        });
    }

    /// <summary>
    /// Record a compared graph as one row of cells — judged leaves, and ignored leaves as unjudged
    /// values — and return whether every judged leaf matched.
    /// </summary>
    public static bool Row(IReadOnlyList<ObjectComparison.Leaf> leaves, int rowIndex)
    {
        foreach (var leaf in leaves)
        {
            if (leaf.Ignored) Value(leaf.Path, leaf.Actual, rowIndex);
            else Cell(leaf.Path, leaf.Matched, leaf.Expected, leaf.Actual, rowIndex);
        }

        return leaves.All(x => x.Matched);
    }

    /// <summary>The mismatched leaves, as one line for a failure message.</summary>
    public static string Describe(IEnumerable<ObjectComparison.Leaf> leaves)
        => string.Join("; ", leaves.Where(x => !x.Matched)
            .Select(x => $"{x.Path}: expected {ObjectComparison.Format(x.Expected)}, was {ObjectComparison.Format(x.Actual)}"));

    /// <summary>An unjudged cell: information beside the verdict, never a check it did not make.</summary>
    public static void Value(string name, object? value, int rowIndex = -1)
    {
        if (!Recording) return;
        ScenarioRecorder.RecordCell(new CellResult(name, ResultStatus.ok, ObjectComparison.Format(value))
        {
            RowIndex = rowIndex
        });
    }
}

using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Wolverine.Bobcat;

/// <summary>
/// An expected value with the members a comparison should show but not judge — a minted id, a
/// timestamp nobody controls (GH-4835). Pass it anywhere an expected object is accepted:
/// <c>ThenEvents(Expect.Value(new AppointmentConfirmed(id, default)).Ignoring(x =&gt; x.ConfirmedAt))</c>.
/// </summary>
/// <remarks>
/// An ignored member is still <em>rendered</em>, as an unjudged value cell: the minted id is exactly
/// the thing a reader wants to see when the spec goes red, so dropping the column would hide it.
/// </remarks>
public interface IExpectedValue
{
    /// <summary>The expected object.</summary>
    object Value { get; }

    /// <summary>Paths (<c>Address.City</c>) shown but not compared. A path also covers everything beneath it.</summary>
    IReadOnlyCollection<string> IgnoredPaths { get; }
}

/// <inheritdoc cref="IExpectedValue" />
public sealed class ExpectedValue<T> : IExpectedValue where T : notnull
{
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);

    public ExpectedValue(T value) => Value = value;

    /// <summary>The expected object.</summary>
    public T Value { get; }

    object IExpectedValue.Value => Value;

    public IReadOnlyCollection<string> IgnoredPaths => _ignored;

    /// <summary>Show <paramref name="member" /> but do not judge it — e.g. <c>x =&gt; x.AssignmentId</c> or <c>x =&gt; x.Address.City</c>.</summary>
    public ExpectedValue<T> Ignoring(Expression<Func<T, object?>> member)
    {
        _ignored.Add(ObjectComparison.PathOf(member));
        return this;
    }

    /// <summary>Show the member at <paramref name="path" /> but do not judge it.</summary>
    public ExpectedValue<T> Ignoring(string path)
    {
        _ignored.Add(path);
        return this;
    }
}

/// <summary>Builds an <see cref="ExpectedValue{T}" />.</summary>
public static class Expect
{
    /// <summary>Expect <paramref name="value" />, to be refined with <c>.Ignoring(...)</c>.</summary>
    public static ExpectedValue<T> Value<T>(T value) where T : notnull => new(value);
}

/// <summary>
/// Structural comparison of two object graphs, property by property and never through
/// <c>Equals</c> (GH-4835): record equality is silently wrong for a collection, which compares
/// by reference.
/// </summary>
/// <remarks>
/// A graph is flattened to its <b>leaves</b> — <c>Address.City</c>, <c>Lines[0].Sku</c>,
/// <c>Lines.Count</c> — and each leaf is compared and rendered as its own cell, so a deep graph is
/// still a cell table rather than one cell holding a document diff.
/// </remarks>
public static class ObjectComparison
{
    /// <summary>How deep a graph is followed, so a cycle cannot hang a comparison.</summary>
    public const int MaxDepth = 8;

    /// <summary>One leaf of a compared graph.</summary>
    public sealed record Leaf(string Path, bool Matched, bool Ignored, object? Expected, object? Actual);

    /// <summary>The leaves of <paramref name="value" />, in declaration order.</summary>
    public static IReadOnlyList<(string Path, object? Value)> Leaves(object? value)
    {
        var leaves = new List<(string, object?)>();
        flatten(value, "", 0, leaves);
        return leaves;
    }

    /// <summary>Compare <paramref name="actual" /> with <paramref name="expected" />, leaf by leaf.</summary>
    public static IReadOnlyList<Leaf> Compare(object? actual, object? expected, IReadOnlyCollection<string>? ignored = null)
    {
        ignored ??= Array.Empty<string>();
        var expectedLeaves = Leaves(expected);
        var actualLeaves = Leaves(actual).ToDictionary(x => x.Path, x => x.Value, StringComparer.Ordinal);

        var results = new List<Leaf>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, value) in expectedLeaves)
        {
            seen.Add(path);
            actualLeaves.TryGetValue(path, out var actualValue);
            var isIgnored = ignored.Any(x => covers(x, path));
            results.Add(new Leaf(path, isIgnored || LeafEquals(actualValue, value), isIgnored, value, actualValue));
        }

        // A leaf only the actual graph has: a longer collection, a non-null branch the expectation left null
        foreach (var (path, value) in actualLeaves.Where(x => !seen.Contains(x.Key)))
        {
            var isIgnored = ignored.Any(x => covers(x, path));
            results.Add(new Leaf(path, isIgnored, isIgnored, null, value));
        }

        return results;
    }

    /// <summary>How a leaf value renders in a cell.</summary>
    public static string Format(object? value) => value switch
    {
        null => "NULL",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary>Two leaves agree: same value, or same text where the types differ only in representation.</summary>
    public static bool LeafEquals(object? actual, object? expected)
    {
        if (actual is null || expected is null) return actual is null && expected is null;
        if (actual.Equals(expected)) return true;
        return string.Equals(Format(actual), Format(expected), StringComparison.Ordinal);
    }

    /// <summary>The dotted path an expression like <c>x =&gt; x.Address.City</c> names.</summary>
    public static string PathOf(LambdaExpression expression)
    {
        var body = expression.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : expression.Body;
        var parts = new Stack<string>();
        while (body is MemberExpression member)
        {
            parts.Push(member.Member.Name);
            body = member.Expression!;
        }

        if (body is not ParameterExpression || parts.Count == 0)
        {
            throw new ArgumentException($"'{expression}' does not name a member path such as x => x.Address.City", nameof(expression));
        }

        return string.Join(".", parts);
    }

    private static bool covers(string ignored, string path)
        => path == ignored || path.StartsWith(ignored + ".", StringComparison.Ordinal) ||
           path.StartsWith(ignored + "[", StringComparison.Ordinal);

    internal static bool IsLeaf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
               type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
               type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan) ||
               type == typeof(Uri) || type == typeof(Type);
    }

    private static void flatten(object? value, string path, int depth, List<(string, object?)> leaves)
    {
        if (value is null || IsLeaf(value.GetType()) || depth >= MaxDepth)
        {
            leaves.Add((path.Length == 0 ? "value" : path, value));
            return;
        }

        if (value is IEnumerable enumerable)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                flatten(item, $"{path}[{index}]", depth + 1, leaves);
                index++;
            }

            leaves.Add(($"{(path.Length == 0 ? "value" : path)}.Count", index));
            return;
        }

        var properties = value.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanRead && x.GetIndexParameters().Length == 0 && x.Name != "EqualityContract")
            .ToArray();

        if (properties.Length == 0)
        {
            // A field-less stub: the type is the whole of what it says
            leaves.Add((path.Length == 0 ? "value" : path, value.GetType().Name));
            return;
        }

        foreach (var property in properties)
        {
            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch (TargetInvocationException e)
            {
                child = $"<{e.InnerException?.GetType().Name ?? "error"}>";
            }

            flatten(child, path.Length == 0 ? property.Name : $"{path}.{property.Name}", depth + 1, leaves);
        }
    }
}

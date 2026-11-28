using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     GH-6005 (jasperfx#1298). The <c>&lt;summary&gt;</c> text of a type, method or property, read from the XML
///     documentation file the compiler writes beside an assembly (<c>GenerateDocumentationFile</c>). The derived
///     rung's source of descriptions: prose the author already wrote, never a guess.
/// </summary>
/// <remarks>
///     Read once per assembly and cached; an assembly with no documentation file beside it describes nothing. The
///     text is flattened: whitespace collapsed, <c>&lt;see cref="…"/&gt;</c> rendered as the referenced name,
///     <c>&lt;paramref&gt;</c> as the parameter's name, every other element as its text.
/// </remarks>
public static class XmlDocComments
{
    private static readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, XElement>?> _members = new();

    /// <summary>The summary of a type, or null when it has none the documentation file knows.</summary>
    public static string? SummaryOf(Type type)
        => memberOf(type.Assembly, "T:" + idOf(type)) is { } member ? summaryText(member) : null;

    /// <summary>
    ///     The summary of a method, by name on its declaring type: the first documented overload of that name,
    ///     because the chain's method is the one the type exposes under it.
    /// </summary>
    public static string? SummaryOf(MethodInfo method)
    {
        if (method.DeclaringType is not { } declaring) return null;
        var members = membersOf(declaring.Assembly);
        if (members is null) return null;

        var prefix = "M:" + idOf(declaring) + "." + method.Name;
        foreach (var (name, element) in members)
        {
            if (name == prefix || name.StartsWith(prefix + "(", StringComparison.Ordinal))
            {
                return summaryText(element);
            }
        }

        return null;
    }

    /// <summary>
    ///     The summary of a property, else the <c>&lt;param&gt;</c> its declaring record's documentation wrote for
    ///     the positional parameter of the same name.
    /// </summary>
    public static string? SummaryOf(PropertyInfo property)
    {
        if (property.DeclaringType is not { } declaring) return null;

        if (memberOf(declaring.Assembly, "P:" + idOf(declaring) + "." + property.Name) is { } member &&
            summaryText(member) is { } summary)
        {
            return summary;
        }

        return ParamOf(declaring, property.Name);
    }

    /// <summary>The summary of a field, else the record's <c>&lt;param&gt;</c> of the same name.</summary>
    public static string? SummaryOf(FieldInfo field)
    {
        if (field.DeclaringType is not { } declaring) return null;

        if (memberOf(declaring.Assembly, "F:" + idOf(declaring) + "." + field.Name) is { } member &&
            summaryText(member) is { } summary)
        {
            return summary;
        }

        return ParamOf(declaring, field.Name);
    }

    /// <summary>The <c>&lt;param name="…"&gt;</c> text a type's documentation wrote for one of its positional parameters.</summary>
    public static string? ParamOf(Type type, string parameterName)
    {
        if (memberOf(type.Assembly, "T:" + idOf(type)) is not { } member) return null;

        var param = member.Elements("param")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("name"), parameterName, StringComparison.Ordinal));

        return param is null ? null : flatten(param);
    }

    private static string? summaryText(XElement member)
    {
        var summary = member.Element("summary");
        return summary is null ? null : flatten(summary);
    }

    private static string? flatten(XElement element)
    {
        var text = new StringBuilder();
        append(element, text);

        var flattened = string.Join(" ", text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flattened.Length == 0 ? null : flattened;

        static void append(XElement node, StringBuilder into)
        {
            foreach (var child in node.Nodes())
            {
                switch (child)
                {
                    case XText t:
                        into.Append(t.Value);
                        break;

                    case XElement e when e.Name.LocalName is "see" or "seealso":
                        into.Append(e.IsEmpty || string.IsNullOrWhiteSpace(e.Value) ? crefName(e) : e.Value);
                        break;

                    case XElement e when e.Name.LocalName is "paramref" or "typeparamref":
                        into.Append((string?)e.Attribute("name"));
                        break;

                    case XElement e:
                        // <para> and <list> read as separate sentences rather than run together
                        if (e.Name.LocalName is "para" or "item") into.Append(' ');
                        append(e, into);
                        if (e.Name.LocalName is "para" or "item") into.Append(' ');
                        break;
                }
            }
        }

        static string crefName(XElement see)
        {
            var cref = (string?)see.Attribute("cref") ?? (string?)see.Attribute("langword") ?? (string?)see.Attribute("href") ?? string.Empty;
            // "T:Ns.Type" -> "Type"; "M:Ns.Type.Method(...)" -> "Method"
            var colon = cref.IndexOf(':');
            if (colon == 1) cref = cref[2..];
            var paren = cref.IndexOf('(');
            if (paren > 0) cref = cref[..paren];
            var dot = cref.LastIndexOf('.');
            return dot >= 0 ? cref[(dot + 1)..] : cref;
        }
    }

    // The documentation id of a type: its full name with nested types joined by '.', generics by their
    // definition (the file documents Foo`1, never Foo<Bar>)
    private static string idOf(Type type)
    {
        if (type.IsGenericType && !type.IsGenericTypeDefinition) type = type.GetGenericTypeDefinition();
        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    private static XElement? memberOf(Assembly assembly, string name)
        => membersOf(assembly) is { } members && members.TryGetValue(name, out var element) ? element : null;

    private static IReadOnlyDictionary<string, XElement>? membersOf(Assembly assembly)
        => _members.GetOrAdd(assembly, static a => read(a));

    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An assembly embedded in a single-file app has no documentation file beside it, and an empty Location is handled as 'none'.")]
    private static IReadOnlyDictionary<string, XElement>? read(Assembly assembly)
    {
        try
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) return null;

            var path = Path.ChangeExtension(assembly.Location, ".xml");
            if (!File.Exists(path)) return null;

            var document = XDocument.Load(path);
            var members = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var member in document.Root?.Element("members")?.Elements("member") ?? Enumerable.Empty<XElement>())
            {
                if ((string?)member.Attribute("name") is { Length: > 0 } name) members.TryAdd(name, member);
            }

            return members;
        }
        catch (Exception)
        {
            // An unreadable documentation file describes nothing; a diagnostic never takes the model down
            return null;
        }
    }
}

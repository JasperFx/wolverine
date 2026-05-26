using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wolverine.Http.Commands;

/// <summary>
/// Post-processes a generated OpenAPI document down to just the HTTP routes whose path template
/// fuzzily matches a user-supplied search term. Used by the <c>openapi --route</c> option to make it
/// easy to inspect the OpenAPI metadata for a single endpoint (or a small related set) when
/// troubleshooting Wolverine.HTTP behavior. Schema components that the retained paths no longer
/// reference are pruned (best-effort) so the output stays focused on the matching routes.
/// </summary>
internal static class OpenApiRouteFilter
{
    /// <summary>
    /// The path templates ("/todoitems/{id}", ...) declared by the document, in document order.
    /// </summary>
    public static IReadOnlyList<string> ListPaths(string documentJson)
    {
        return JsonNode.Parse(documentJson) is JsonObject { } root && root["paths"] is JsonObject paths
            ? paths.Select(pair => pair.Key).ToArray()
            : [];
    }

    /// <summary>
    /// Returns the document filtered to only the paths whose template contains <paramref name="routeSearch"/>
    /// (case-insensitive). <paramref name="matchedPaths"/> reports which path templates were kept.
    /// </summary>
    public static string Filter(string documentJson, string routeSearch, out IReadOnlyList<string> matchedPaths)
    {
        var root = JsonNode.Parse(documentJson) as JsonObject
                   ?? throw new InvalidOperationException("The generated OpenAPI document was not a JSON object.");

        var search = routeSearch.Trim();
        var matched = new List<string>();

        if (root["paths"] is JsonObject paths)
        {
            var toRemove = new List<string>();
            foreach (var entry in paths)
            {
                if (entry.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(entry.Key);
                }
                else
                {
                    toRemove.Add(entry.Key);
                }
            }

            foreach (var key in toRemove)
            {
                paths.Remove(key);
            }

            if (matched.Count > 0)
            {
                try
                {
                    PruneUnreferencedComponents(root, paths);
                }
                catch (Exception)
                {
                    // Best-effort only: leave the components intact on any failure. An over-broad
                    // components section is still a valid OpenAPI document.
                }
            }
        }

        matchedPaths = matched;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void PruneUnreferencedComponents(JsonObject root, JsonObject retainedPaths)
    {
        if (root["components"] is not JsonObject components)
        {
            return;
        }

        // Walk the retained paths and collect every "$ref" they reach, transitively through the
        // components they point at, so component-to-component references are preserved.
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var toScan = new Queue<JsonNode>();
        toScan.Enqueue(retainedPaths);

        while (toScan.Count > 0)
        {
            foreach (var pointer in FindRefs(toScan.Dequeue()))
            {
                if (referenced.Add(pointer) && ResolvePointer(root, pointer) is { } resolved)
                {
                    toScan.Enqueue(resolved);
                }
            }
        }

        foreach (var section in components.ToList())
        {
            if (section.Value is not JsonObject sectionObject)
            {
                continue;
            }

            var unreferenced = sectionObject
                .Where(item => !referenced.Contains($"#/components/{section.Key}/{item.Key}"))
                .Select(item => item.Key)
                .ToList();

            foreach (var name in unreferenced)
            {
                sectionObject.Remove(name);
            }

            if (sectionObject.Count == 0)
            {
                components.Remove(section.Key);
            }
        }

        if (components.Count == 0)
        {
            root.Remove("components");
        }
    }

    private static IEnumerable<string> FindRefs(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    if (pair.Key == "$ref" && pair.Value is JsonValue value &&
                        value.TryGetValue<string>(out var reference))
                    {
                        yield return reference;
                    }
                    else
                    {
                        foreach (var nested in FindRefs(pair.Value))
                        {
                            yield return nested;
                        }
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var nested in FindRefs(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static JsonNode? ResolvePointer(JsonObject root, string pointer)
    {
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        JsonNode? current = root;
        foreach (var rawSegment in pointer[2..].Split('/'))
        {
            // JSON Pointer escaping: ~1 => '/', ~0 => '~'
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
            if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
            {
                current = next;
            }
            else
            {
                return null;
            }
        }

        return current;
    }
}

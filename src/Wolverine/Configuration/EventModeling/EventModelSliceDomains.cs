using System.Reflection;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     GH-4829. Gives the slices Wolverine derives their declared <see cref="EventModelSliceDescriptor.Domain" />,
///     and keeps the slices of a message handled by several modules apart.
/// </summary>
/// <remarks>
///     <para>
///         <b>A module is a Domain, and membership is declared, never inferred.</b> The only inputs are
///         <see cref="DomainAttribute" /> on a handler or endpoint and the policies a declared Event Model
///         carries on <see cref="EventModelDescriptor.DomainAssignments" />; <see cref="EventModelDomains.Resolve" />
///         applies them. Nothing here guesses a domain from a namespace or an assembly on its own.
///     </para>
///     <para>
///         <b>Why the names.</b> A slice's name is the merge key. Under
///         <c>MultipleHandlerBehavior.Separated</c> every module's handler of <c>OrderPlaced</c> is its own
///         sticky chain, and naming each of them <c>OrderPlaced</c> folded them into one slice that named
///         the first handler and claimed every module's output. Each such slice is now qualified —
///         <c>OrderPlaced (Billing)</c> by its declared domain, else <c>OrderPlaced (Billing.OrderPlacedHandler)</c>
///         by its handler type — and a message with a single handler chain keeps its bare name, so the
///         joins that already work are not moved.
///     </para>
/// </remarks>
public static class EventModelSliceDomains
{
    /// <summary>
    ///     Every domain policy the application's <em>declared</em> Event Models carry — the sources on the
    ///     <see cref="EventModelProvenance.Declared" /> rung, such as <c>services.AddEventModel(...)</c>.
    /// </summary>
    /// <remarks>
    ///     Read straight off the declared sources rather than the assembled model, because the derived
    ///     sources are the ones asking: they are part of that assembly, and need the policies first.
    /// </remarks>
    public static async Task<IReadOnlyList<DomainAssignmentDescriptor>> DeclaredAssignmentsAsync(
        IServiceProvider services, CancellationToken token = default)
    {
        var assignments = new List<DomainAssignmentDescriptor>();

        foreach (var source in services.GetServices<IEventModelDefinitionSource>())
        {
            if (source.Provenance != EventModelProvenance.Declared) continue;

            var descriptor = await source.TryCreateAsync(services, token).ConfigureAwait(false);
            if (descriptor is null) continue;

            foreach (var assignment in descriptor.DomainAssignments)
            {
                if (!assignments.Contains(assignment)) assignments.Add(assignment);
            }
        }

        return assignments;
    }

    /// <summary>
    ///     Resolve the declared domain of <paramref name="handlerType" /> (and, optionally, of one of its
    ///     methods) and put it on <paramref name="slice" />. A declaration the winner overrode, or two that
    ///     tie on different domains, is called out as a hotspot on the slice rather than dropped.
    /// </summary>
    public static EventModelSliceDescriptor ApplyDomain(EventModelSliceDescriptor slice, Type? handlerType,
        MethodInfo? handlerMethod, IReadOnlyList<DomainAssignmentDescriptor> assignments)
    {
        if (handlerType is null) return slice;

        var resolution = EventModelDomains.Resolve(handlerType, handlerMethod, assignments);
        if (resolution.Source == DomainResolutionSource.None) return slice;

        var hotspots = new List<HotspotDescriptor>();
        var handler = handlerMethod is null ? handlerType.FullName : $"{handlerType.FullName}.{handlerMethod.Name}()";

        if (resolution.IsAmbiguous)
        {
            hotspots.Add(HotspotDescriptor.Prose(
                $"Domain: {handler} is declared in more than one domain ({string.Join(", ", resolution.Conflicts.Select(x => $"'{x}'"))}) by declarations of equal weight ({describe(resolution.Source)}), so no domain was chosen."));
        }
        else if (resolution.Conflicts.Count > 0)
        {
            hotspots.Add(HotspotDescriptor.Prose(
                $"Domain: {handler} is in '{resolution.Domain}' by its {describe(resolution.Source)}, which overrode other declarations putting it in {string.Join(", ", resolution.Conflicts.Select(x => $"'{x}'"))}."));
        }

        return slice with
        {
            Domain = resolution.Domain ?? slice.Domain,
            Hotspots = hotspots.Count == 0 ? slice.Hotspots : slice.Hotspots.Concat(hotspots).ToList()
        };
    }

    /// <summary>
    ///     Give every slice in a group that would otherwise share one name a name of its own: the bare
    ///     name qualified by its domain when that is unique in the group, else by its handler type, else
    ///     by its sticky endpoint. A slice none of those tell apart keeps the bare name, and the merge's
    ///     collision guard (jasperfx#954) reports it rather than folding it into another.
    /// </summary>
    /// <param name="group">Slices derived under the same name, each with the handler type and the endpoint its chain is stuck to.</param>
    internal static IReadOnlyList<EventModelSliceDescriptor> QualifyNames(
        IReadOnlyList<(EventModelSliceDescriptor Slice, string? HandlerType, Uri? Endpoint)> group)
    {
        if (group.Count < 2) return group.Select(x => x.Slice).ToList();

        var domains = countOf(group.Select(x => x.Slice.Domain));
        var handlers = countOf(group.Select(x => x.HandlerType));
        var endpoints = countOf(group.Select(x => x.Endpoint?.ToString()));

        return group.Select(member =>
        {
            var qualifier = unique(member.Slice.Domain, domains)
                            ?? unique(member.HandlerType, handlers)
                            ?? unique(member.Endpoint?.ToString(), endpoints);

            return qualifier is null ? member.Slice : member.Slice with { Name = Qualify(member.Slice.Name, qualifier) };
        }).ToList();

        static Dictionary<string, int> countOf(IEnumerable<string?> values)
            => values.Where(x => x is not null).GroupBy(x => x!, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

        static string? unique(string? value, Dictionary<string, int> counts)
            => value is not null && counts.TryGetValue(value, out var count) && count == 1 ? value : null;
    }

    /// <summary>The name of one of several slices for the same message: <c>OrderPlaced (Billing)</c>.</summary>
    public static string Qualify(string sliceName, string qualifier) => $"{sliceName} ({qualifier})";

    private static string describe(DomainResolutionSource source) => source switch
    {
        DomainResolutionSource.MethodAttribute => "[Domain] attribute on the method",
        DomainResolutionSource.TypeAttribute => "[Domain] attribute on the class",
        DomainResolutionSource.TypePolicy => "type policy",
        DomainResolutionSource.NamespacePolicy => "namespace policy",
        DomainResolutionSource.AssemblyPolicy => "assembly policy",
        _ => source.ToString()
    };
}

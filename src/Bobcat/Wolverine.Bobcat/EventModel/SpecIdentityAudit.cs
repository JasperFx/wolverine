using System.Reflection;
using System.Text;
using JasperFx.Events.EventModeling;

namespace Bobcat;

/// <summary>One spec identity and the slice that claims it.</summary>
/// <param name="Identity">The <c>{Feature}/{Scenario}</c> identity.</param>
/// <param name="Slice">The slice the claim sits on.</param>
/// <param name="Pending">
/// True when the claim is a <b>pending</b> specification — a scenario with no steps, which
/// upstream carries as a hotspot rather than as a specification (jasperfx#689). The identity is
/// still real and still joins; it simply verifies nothing yet.
/// </param>
public sealed record SpecIdentityClaim(string Identity, string Slice, bool Pending = false);

/// <summary>An identity both sides declare, on different slices.</summary>
public sealed record MisboundSpecIdentity(string Identity, string DeclaredOn, string BoundTo);

/// <summary>
/// The join nothing checked (issue #338): every <c>{Feature}/{Scenario}</c> identity the compiled
/// specs declare, against every one the design declares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a gap and not a user error.</b> Both artifacts already exist, and the join is
/// pure computation — the generated <c>BobcatEventModelSource</c> says what the tests declare, the
/// curated model says what the design declares, and <c>{Feature}/{Scenario}</c> is deliberately
/// the same string on both sides so they join with no mapping table. A projected spec whose
/// scenario nobody designed survived four commits in CritterCrush with a green suite, a clean
/// build, no diagnostic and no warning; the only thing that caught it was a human diffing
/// generated code against the model's scenario names.
/// </para>
/// <para>
/// <b>Two directions, and they are not the same finding.</b> An <see cref="Orphans">orphan</see>
/// is a test identity the model does not declare — the canvas shows a specification for something
/// nobody designed. A <see cref="Holes">hole</see> is a model scenario no test covers — the design
/// declares behaviour nothing verifies. <see cref="Misbound"/> is the third, free once both sides
/// are in hand: an identity both declare, bound to different slices, which is a spec pointing at
/// the wrong behaviour on the canvas.
/// </para>
/// <para>
/// <b>Why the existing diagnostics do not cover it.</b> <c>BOBCAT025</c> and <c>BOBCAT026</c>
/// compare slice NAMES. A test bound to the right slice under a scenario name nobody designed
/// passes both.
/// </para>
/// <para>
/// <b>Where it runs.</b> Not a build diagnostic: the generator holds the compiled identities but
/// not the curated model, which may not even live in the spec project. It is pure computation over
/// two <see cref="EventModelDescriptor"/>s, so the spec assembly's own test run is the one place
/// both facts are already loaded — one assertion, and the drift is a red build. The same call
/// answers a Stoat gate, since <see cref="Drifted"/> and <see cref="Report"/> are the whole
/// contract.
/// </para>
/// </remarks>
/// <param name="Orphans">Test identities the model does not declare.</param>
/// <param name="Holes">Model identities no test covers.</param>
/// <param name="Misbound">Identities both sides declare, on different slices.</param>
/// <param name="Pending">Identities that join, whose test scenario has no steps.</param>
/// <param name="Excused">Identities excused by name, with the reason given for each.</param>
/// <param name="Matched">How many identities joined cleanly.</param>
public sealed record SpecIdentityAudit(
    IReadOnlyList<SpecIdentityClaim> Orphans,
    IReadOnlyList<SpecIdentityClaim> Holes,
    IReadOnlyList<MisboundSpecIdentity> Misbound,
    IReadOnlyList<SpecIdentityClaim> Pending,
    IReadOnlyList<KeyValuePair<string, string>> Excused,
    int Matched)
{
    /// <summary>
    /// Whether the two sides disagree about anything. A pending specification is deliberately not
    /// drift: the identity joins, and "this scenario has no steps yet" is already a hotspot on the
    /// canvas — failing a build for it would make the audit an unrelated policy.
    /// </summary>
    public bool Drifted => Orphans.Count > 0 || Holes.Count > 0 || Misbound.Count > 0;

    /// <summary>
    /// Compare what the design declares against what the compiled specs declare.
    /// </summary>
    /// <param name="declared">
    /// The design's model. A curated file becomes one through
    /// <c>Bobcat.EventModel.ImportedModelMapper.ToDescriptor</c>.
    /// </param>
    /// <param name="tests">The spec assembly's generated descriptor.</param>
    /// <param name="excused">
    /// Identities to leave out of both directions, each with the reason it is excused — a
    /// chapter-wide invariant test that deliberately binds no slice, or a model scenario
    /// specified in a lane Bobcat cannot see. Said out loud here rather than inferred from
    /// silence, because "deliberately not a spec" is a claim someone should have to make.
    /// </param>
    public static SpecIdentityAudit Compare(
        EventModelDescriptor declared,
        EventModelDescriptor tests,
        IReadOnlyDictionary<string, string>? excused = null)
        => Compare(ClaimsIn(declared), ClaimsIn(tests), excused);

    /// <summary>
    /// Compare the design's model against the generated descriptors of one or more spec
    /// assemblies — the form a spec assembly's own test uses, with
    /// <c>typeof(SomeSpec).Assembly</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No assembly passed has a generated Event Model source. That is never a clean result: the
    /// audit would report every declared scenario as a hole, which is the same confident lie as
    /// zero-filling an unmeasured duration.
    /// </exception>
    public static SpecIdentityAudit Compare(
        EventModelDescriptor declared,
        IReadOnlyDictionary<string, string>? excused,
        params Assembly[] specAssemblies)
    {
        if (specAssemblies.Length == 0)
            throw new InvalidOperationException("Name at least one spec assembly to audit.");

        var claims = new List<SpecIdentityClaim>();
        var found = new List<string>();

        foreach (var assembly in specAssemblies)
        {
            var descriptor = GeneratedEventModel.For(assembly);
            if (descriptor is null) continue;

            found.Add(assembly.GetName().Name ?? "?");
            claims.AddRange(ClaimsIn(descriptor));
        }

        if (found.Count == 0)
        {
            throw new InvalidOperationException(
                $"None of {string.Join(", ", specAssemblies.Select(x => x.GetName().Name))} has a generated "
                + $"Event Model source ({GeneratedEventModel.GeneratedSourceTypeName}). The generator emits "
                + "one only for an assembly whose specs declare slices, so either the assembly named is not "
                + "the spec assembly, or no test in it is bound to a slice: bind one with "
                + "[BobcatSpec(typeof(TheCommand))] or [BobcatSlice(typeof(TheCommand))]. Auditing against nothing "
                + "would report every declared scenario as uncovered.");
        }

        return Compare(ClaimsIn(declared), claims, excused);
    }

    /// <inheritdoc cref="Compare(EventModelDescriptor,EventModelDescriptor,IReadOnlyDictionary{string,string})"/>
    public static SpecIdentityAudit Compare(EventModelDescriptor declared, Assembly specAssembly)
        => Compare(declared, null, specAssembly);

    /// <summary>The comparison itself, over claims from wherever they came.</summary>
    public static SpecIdentityAudit Compare(
        IEnumerable<SpecIdentityClaim> declared,
        IEnumerable<SpecIdentityClaim> tests,
        IReadOnlyDictionary<string, string>? excused = null)
    {
        var pardons = excused ?? new Dictionary<string, string>();

        // Grouped by identity rather than kept as a flat list: a slice may legitimately be
        // described by several features, and one identity may be claimed by more than one slice
        // on either side. Comparing the SETS is what makes "bound to the wrong slice" a finding
        // instead of a coin toss over which claim was seen first.
        var left = group(declared.Where(x => !pardons.ContainsKey(x.Identity)));
        var right = group(tests.Where(x => !pardons.ContainsKey(x.Identity)));

        var orphans = new List<SpecIdentityClaim>();
        var holes = new List<SpecIdentityClaim>();
        var misbound = new List<MisboundSpecIdentity>();
        var pending = new List<SpecIdentityClaim>();
        var matched = 0;

        foreach (var (identity, claims) in right)
        {
            if (!left.TryGetValue(identity, out var design))
            {
                orphans.AddRange(claims);
                continue;
            }

            matched++;
            pending.AddRange(claims.Where(x => x.Pending));

            var designSlices = design.Select(x => x.Slice).ToHashSet(StringComparer.Ordinal);
            foreach (var claim in claims.Where(x => !designSlices.Contains(x.Slice)))
            {
                misbound.Add(new MisboundSpecIdentity(
                    identity, string.Join(", ", designSlices.OrderBy(x => x, StringComparer.Ordinal)), claim.Slice));
            }
        }

        foreach (var (identity, claims) in left.Where(x => !right.ContainsKey(x.Key)))
        {
            holes.AddRange(claims);
        }

        return new SpecIdentityAudit(
            orphans, holes, misbound, pending,
            pardons.OrderBy(x => x.Key, StringComparer.Ordinal).ToList(),
            matched);
    }

    /// <summary>
    /// Every identity a descriptor claims: its slices' <c>Specifications</c>, plus the pending
    /// ones, which upstream carries as hotspots (<c>HotspotDescriptor.PendingSpecification</c>).
    /// </summary>
    /// <remarks>
    /// Pending identities are collected deliberately. Leaving them out would report a step-less
    /// scenario as a hole — "the design declares behaviour nothing verifies" — which reads as a
    /// missing test when the test is right there with nothing in it. Two different problems, and
    /// the audit's job is identities; the empty body is already a hotspot.
    /// </remarks>
    public static IReadOnlyList<SpecIdentityClaim> ClaimsIn(EventModelDescriptor descriptor)
    {
        var claims = new List<SpecIdentityClaim>();

        foreach (var slice in descriptor.Slices)
        {
            foreach (var specification in slice.Specifications)
            {
                claims.Add(new SpecIdentityClaim(specification.Identity, slice.Name));
            }

            foreach (var hotspot in slice.Hotspots)
            {
                if (hotspot.Origin != HotspotOrigin.PendingSpecification) continue;
                if (hotspot.SpecificationIdentity is not { } identity) continue;

                claims.Add(new SpecIdentityClaim(identity, slice.Name, Pending: true));
            }
        }

        return claims;
    }

    /// <summary>One line per finding, and a count when there is nothing to say.</summary>
    public string Report()
    {
        var sb = new StringBuilder();

        sb.AppendLine(Drifted
            ? $"Spec identities: {Matched} matched, {Orphans.Count} orphaned, {Holes.Count} uncovered, {Misbound.Count} misbound."
            : $"Spec identities: {Matched} matched, no drift.");

        section(sb, "Orphans — a test declares this identity and the model does not",
            Orphans.Select(x => $"{x.Identity}  (bound to slice {x.Slice})"));

        section(sb, "Holes — the model declares this identity and no test covers it",
            Holes.Select(x => $"{x.Identity}  (slice {x.Slice})"));

        section(sb, "Misbound — both declare this identity, on different slices",
            Misbound.Select(x => $"{x.Identity}  (model: {x.DeclaredOn}; test: {x.BoundTo})"));

        section(sb, "Pending — the identity joins, but the scenario has no steps",
            Pending.Select(x => $"{x.Identity}  (slice {x.Slice})"));

        section(sb, "Excused", Excused.Select(x => $"{x.Key}  — {x.Value}"));

        return sb.ToString();
    }

    private static void section(StringBuilder sb, string title, IEnumerable<string> lines)
    {
        var rendered = lines.ToList();
        if (rendered.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine(title + ":");
        foreach (var line in rendered) sb.AppendLine("  " + line);
    }

    private static Dictionary<string, List<SpecIdentityClaim>> group(IEnumerable<SpecIdentityClaim> claims)
    {
        var grouped = new Dictionary<string, List<SpecIdentityClaim>>(StringComparer.Ordinal);

        foreach (var claim in claims)
        {
            if (!grouped.TryGetValue(claim.Identity, out var list))
            {
                grouped[claim.Identity] = list = new List<SpecIdentityClaim>();
            }

            list.Add(claim);
        }

        return grouped;
    }
}

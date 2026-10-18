using Bobcat;
using Bobcat.Generated.EventModel;
using JasperFx.Events.EventModeling;

namespace BankAccountES.Tests;

/// <summary>
/// The Bobcat-side half of the multi-source event-model vehicle (bobcat#172). Assembles one
/// provenance-stamped <see cref="EventModelDescriptor"/> from the four design-time sources --
/// the running host's Wolverine/HTTP chains (Derived), the store's projection registry (Derived,
/// jasperfx#825 / bobcat#300), the C# overlay in Program.cs (Declared), and this assembly's
/// generated <c>BobcatEventModelSource</c> (Declared) -- exactly the way
/// <c>EventModelDiscovery</c> would, so <see cref="EventModel"/> can assert that the merge
/// attributes every role, keeps declarations where nothing outranks them, and surfaces the
/// planted disagreement (FreezeAccount.cs) as a hotspot instead of swallowing it.
/// </summary>
/// <remarks>
/// <para>
/// The generated source is <c>internal</c> to this assembly and the host cannot reference its own
/// spec project, so this class -- running where both the booted host's container and the
/// generated source are reachable -- is currently the only place all the sources can compose.
/// </para>
/// <para>
/// <b>Every type name here is a string, never a type argument.</b> These steps assert on the model, so
/// they carry no <see cref="EventModelRoleAttribute"/>: contributing to the model they read would make
/// the vehicle observe itself. Every step reports through <see cref="SpecAssert"/>, so a spec shows every
/// disagreement it reaches rather than only its first.
/// </para>
/// </remarks>
public class EventModelSteps(BankAccountHost app)
{
    private IReadOnlyList<EventModelDescriptor> _models = [];
    private HotspotDescriptor? _disagreement;

    private EventModelDescriptor? model()
    {
        if (_models.Count == 1) return _models[0];
        SpecAssert.Fail($"Expected exactly one assembled model, but got [{string.Join(", ", _models.Select(m => m.Name))}] -- assemble first, and check every source names the same model.");
        return null;
    }

    private EventModelSliceDescriptor? sliceNamed(string name)
    {
        if (model() is not { } m) return null;
        var slice = m.Slices.FirstOrDefault(s => s.Name == name);
        if (slice is null) SpecAssert.Fail($"No slice named '{name}'. Slices: {string.Join(", ", m.Slices.Select(s => s.Name))}");
        return slice;
    }

    [When("the event model is assembled from the chains, the overlay, the store and this assembly's specs")]
    public async Task AssembleEventModel()
    {
        var services = app.Host.Services;

        // What EventModelDiscovery.AssembleAsync(services) would do, plus this assembly's
        // generated source -- which the host's container cannot see (see the class remarks).
        // DiscoverAsync finds every IEventModelDefinitionSource the host registered: Wolverine's
        // chains, the overlay, and -- since JasperFx.Events 2.69 (jasperfx#825, bobcat#300) -- the
        // store's projection registry, which AddMarten / AddFisher register unasked.
        var discovered = (await EventModelDiscovery.DiscoverAsync(services, CancellationToken.None)).ToList();

        // Provenance is a default interface member, so it is only reachable through the interface.
        IEventModelDefinitionSource specSource = BobcatEventModelSource.Instance;
        var specs = await specSource.TryCreateAsync(services, CancellationToken.None);
        if (specs is not null) discovered.Add(specs.WithProvenance(specSource.Provenance));

        _models = EventModelDiscovery.Assemble(discovered);

        // Vehicle plumbing: with BOBCAT_EVENT_MODEL_EXPORT set to a file path, write the composed
        // model as the same camelCase/PascalCase-enum JSON the console's PUT /api/event-model
        // accepts -- this process is the only one that can see every source (see the class
        // remarks), so this file is how the merged picture reaches a viewer today.
        if (Environment.GetEnvironmentVariable("BOBCAT_EVENT_MODEL_EXPORT") is { Length: > 0 } path && _models.Count == 1)
        {
            var wire = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            };
            wire.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(_models[0], wire));
        }
    }

    [Then("there is exactly one model, named {name}")]
    public void ThereIsExactlyOneModelNamed(string name)
        => SpecAssert.Fact(_models.Count == 1 && _models[0].Name == name,
            $"Expected one model named '{name}', but got: [{string.Join(", ", _models.Select(m => m.Name))}]");

    [Then("the {slice} slice's {role} role is claimed by {provenance}")]
    public void TheRoleIsClaimedBy(string slice, EventModelRole role, EventModelProvenance provenance)
    {
        if (sliceNamed(slice) is not { } s) return;
        var actual = s.ProvenanceFor(role);
        SpecAssert.Fact(actual == provenance, $"{slice}.{role} is claimed by {actual?.ToString() ?? "nobody"}, expected {provenance}.");
    }

    [Then("every claimed role on every slice names its source")]
    public void EveryClaimedRoleNamesItsSource()
    {
        if (model() is not { } m) return;
        var unattributed = (from slice in m.Slices
            from role in Enum.GetValues<EventModelRole>()
            where slice.Claims(role) && slice.ProvenanceFor(role) is null
            select $"{slice.Name}.{role}").ToList();

        SpecAssert.Fact(unattributed.Count == 0, $"Roles with no source: {string.Join(", ", unattributed)}");
    }

    /// <summary>
    /// The vehicle plants exactly one disagreement (FreezeAccount.cs), so "the" disagreement is
    /// well-defined; a second one appearing anywhere in the model is itself a finding this fails on. (The
    /// Query-vs-View pattern disagreement every GET slice carried until wolverine#4975 used to be excepted here.)
    /// </summary>
    [Then("the {slice} slice reports a source disagreement on {role}")]
    public void TheSliceReportsADisagreementOn(string slice, EventModelRole role)
    {
        if (sliceNamed(slice) is not { } s || model() is not { } m) return;

        var onRole = s.Hotspots.Where(h => h.Origin == HotspotOrigin.SourceDisagreement && h.Role == role).ToList();
        if (!SpecAssert.Fact(onRole.Count > 0, $"No SourceDisagreement hotspot on {slice}.{role}. Hotspots: {describeHotspots(s)}")) return;

        var all = m.Slices.SelectMany(x => x.Hotspots.Where(h => h.Origin == HotspotOrigin.SourceDisagreement)).ToList();
        if (SpecAssert.Fact(all.Count == 1,
                $"Expected exactly one SourceDisagreement in the whole model, found {all.Count}: {string.Join(" | ", all.Select(h => h.Text))}"))
        {
            _disagreement = all[0];
        }
    }

    [Then("that disagreement kept the {provenance} claim naming {value}")]
    public void ThatDisagreementKept(EventModelProvenance provenance, string value)
    {
        var kept = _disagreement?.WinningClaim;
        SpecAssert.Fact(kept is not null && kept.Provenance == provenance && kept.Value.Contains(value),
            $"Winning claim is {kept?.Provenance} '{kept?.Value}', expected {provenance} naming '{value}'.");
    }

    [Then("that disagreement dropped the {provenance} claim {value}")]
    public void ThatDisagreementDropped(EventModelProvenance provenance, string value)
    {
        var lost = _disagreement?.LosingClaim;
        SpecAssert.Fact(lost is not null && lost.Provenance == provenance && lost.Value == value,
            $"Losing claim is {lost?.Provenance} '{lost?.Value}', expected {provenance} '{value}'.");
    }

    /// <summary>
    /// bobcat#501 and wolverine#4975: the specs say a GET slice is a Query, and Wolverine derives the GET chain as a
    /// Query too, so the merged slice is a Query with no Pattern disagreement. Until wolverine#4975 the derived View
    /// outranked the specs' Query and this step pinned that disagreement instead.
    /// </summary>
    [Then("the {slice} slice is a Query, and the sources agree on its pattern")]
    public void TheGetSliceIsAQueryTheSourcesAgreeOn(string slice)
    {
        if (sliceNamed(slice) is not { } s) return;

        SpecAssert.Fact(s.Pattern == SlicePattern.Query, $"{slice}.Pattern is {s.Pattern?.ToString() ?? "null"}, expected Query.");
        var onPattern = s.Hotspots.Where(h => h.Origin == HotspotOrigin.SourceDisagreement && h.Role == EventModelRole.Pattern).ToList();
        SpecAssert.Fact(onPattern.Count == 0,
            $"Expected no Pattern disagreement on {slice}. Hotspots: {describeHotspots(s)}");
    }

    /// <summary>
    /// bobcat#501: what this assembly's specs alone say about a GET slice, before the merge -- a Query that reads
    /// <paramref name="type"/> through <c>ReadsFrom</c>, produces no read model, decides against no aggregate and
    /// consumes none of the events its specs arranged.
    /// </summary>
    [Then("this assembly's specs alone say the {slice} slice is a Query reading {type}")]
    public void TheSpecsAloneSayTheSliceIsAQueryReading(string slice, string type)
    {
        var declared = BobcatEventModelSource.Describe().Slices.FirstOrDefault(s => s.Name == slice);
        if (declared is null)
        {
            SpecAssert.Fail($"The specs declare no slice named '{slice}'.");
            return;
        }

        SpecAssert.Fact(declared.Pattern == SlicePattern.Query, $"The specs say {slice} is {declared.Pattern?.ToString() ?? "null"}, expected Query.");
        var reads = declared.ReadsFrom.Select(t => t.Name).ToList();
        SpecAssert.Fact(reads.Contains(type), $"The specs say {slice} reads from [{string.Join(", ", reads)}], expected '{type}'.");
        SpecAssert.Fact(declared.ReadModelTypes.Count == 0,
            $"The specs say {slice} produces [{string.Join(", ", declared.ReadModelTypes.Select(t => t.Name))}]; a Query reads, it produces nothing.");
        SpecAssert.Fact(declared.ConsumedEvents.Count == 0,
            $"The specs say {slice} consumes [{string.Join(", ", declared.ConsumedEvents.Select(t => t.Name))}]; a Query's arranged events are context.");
        SpecAssert.Fact(declared.AggregateTypes.Count == 0,
            $"The specs say {slice} decides against [{string.Join(", ", declared.AggregateTypes.Select(t => t.Name))}]; a Query decides nothing.");
        SpecAssert.Fact(declared.PatternViolations.Count == 0,
            $"The specs' {slice} Query has pattern violations: {string.Join(" | ", declared.PatternViolations.Select(h => h.Text))}");
    }

    [Then("the {slice} slice reports no source disagreement")]
    public void TheSliceReportsNoDisagreement(string slice)
    {
        if (sliceNamed(slice) is not { } s) return;
        SpecAssert.Fact(s.Hotspots.All(h => h.Origin != HotspotOrigin.SourceDisagreement),
            $"Unexpected disagreement(s) on {slice}: {describeHotspots(s)}");
    }

    [Then("the {slice} slice is in domain {domain}")]
    public void TheSliceIsInDomain(string slice, string domain)
    {
        if (sliceNamed(slice) is not { } s) return;
        SpecAssert.Check("domain", s.Domain, domain);
    }

    [Then("the {slice} slice is triggered by {label}")]
    public void TheSliceIsTriggeredBy(string slice, string label)
    {
        if (sliceNamed(slice) is not { } s) return;
        SpecAssert.Check("label", s.TriggerLabel, label);
    }

    /// <summary>
    /// Assert which read model a slice reads -- identity, not provenance (bobcat#175). A derived read model
    /// could be wrong, ugly or missing while every provenance claim still passed; wolverine#4182 was found by
    /// looking at the canvas, which is exactly the kind of catch a spec is supposed to make instead.
    /// </summary>
    [Then("the {slice} slice reads the {readModel} read model")]
    public void TheSliceReadsTheReadModel(string slice, string readModel)
    {
        if (sliceNamed(slice) is not { } s) return;
        var names = s.ReadModelTypes.Select(t => t.Name).ToList();
        SpecAssert.Fact(names.Contains(readModel), $"{slice} reads [{string.Join(", ", names)}], expected '{readModel}'.");
    }

    /// <summary>
    /// bobcat#300. Slices merge by name, and the store-derived View slice is named after its document type
    /// -- the same name <see cref="AccountView"/> binds. Two slices here means the convention broke and the
    /// canvas shows two stickies for one projection.
    /// </summary>
    [Then("there is exactly one slice named {name}")]
    public void ThereIsExactlyOneSliceNamed(string name)
    {
        if (model() is not { } m) return;
        var count = m.Slices.Count(s => s.Name == name);
        SpecAssert.Fact(count == 1,
            $"Expected exactly one slice named '{name}', found {count}. Slices: {string.Join(", ", m.Slices.Select(s => s.Name))}");
    }

    [Then("the {slice} slice has pattern {pattern}")]
    public void TheSliceHasPattern(string slice, SlicePattern pattern)
    {
        if (sliceNamed(slice) is not { } s) return;
        SpecAssert.Fact(s.Pattern == pattern, $"{slice}.Pattern is {s.Pattern?.ToString() ?? "null"}, expected {pattern}.");
    }

    [Then("the {slice} slice consumes the {eventName} event")]
    public void TheSliceConsumes(string slice, string eventName)
    {
        if (sliceNamed(slice) is not { } s) return;
        var names = s.ConsumedEvents.Select(t => t.Name).ToList();
        SpecAssert.Fact(names.Contains(eventName), $"{slice} consumes [{string.Join(", ", names)}], expected '{eventName}'.");
    }

    /// <summary>
    /// bobcat#297: what this assembly's specs say on their own, before the merge -- the generated source
    /// read directly. A View spec's arranged events are its ConsumedEvents.
    /// </summary>
    [Then("this assembly's specs alone say the {slice} slice consumes the {eventName} event")]
    public void TheSpecsAloneSayTheSliceConsumes(string slice, string eventName)
    {
        var declared = BobcatEventModelSource.Describe().Slices.FirstOrDefault(s => s.Name == slice);
        if (declared is null)
        {
            SpecAssert.Fail($"The specs declare no slice named '{slice}'.");
            return;
        }

        var names = declared.ConsumedEvents.Select(t => t.Name).ToList();
        SpecAssert.Fact(names.Contains(eventName), $"The specs say {slice} consumes [{string.Join(", ", names)}], expected '{eventName}'.");
    }

    [Then("the {slice} slice reads from {type}")]
    public void TheSliceReadsFrom(string slice, string type)
    {
        if (sliceNamed(slice) is not { } s) return;
        var reads = s.ReadsFrom.Select(t => t.Name).ToList();
        SpecAssert.Fact(reads.Contains(type), $"{slice} reads from [{string.Join(", ", reads)}], expected '{type}'.");
    }

    /// <summary>
    /// bobcat#501: the whole ReadsFrom list, in order, written "A, B" -- a Query's arranged GivenEvents stream must not
    /// be in it.
    /// </summary>
    [Then("the {slice} slice reads from exactly {types}")]
    public void TheSliceReadsFromExactly(string slice, string types)
    {
        if (sliceNamed(slice) is not { } s) return;
        var reads = string.Join(", ", s.ReadsFrom.Select(t => t.Name));
        SpecAssert.Fact(reads == types, $"{slice} reads from [{reads}], expected exactly [{types}].");
    }

    [Then("there is no {kind} link to the {to} slice via {via}")]
    public void ThereIsNoLinkTo(EventModelLinkKind kind, string to, string via)
    {
        if (model() is not { } m) return;
        var links = m.Links.Where(l => l.Kind == kind && l.ToSlice == to && l.Via.Name == via).ToList();
        SpecAssert.Fact(links.Count == 0,
            $"Unexpected {kind} link(s) to {to} via {via}: {string.Join(" | ", links.Select(l => $"{l.FromSlice} -> {l.ToSlice}"))}");
    }

    [Then("{type} is not an aggregate of the {slice} slice")]
    public void IsNotAnAggregateOf(string type, string slice)
    {
        if (sliceNamed(slice) is not { } s) return;
        var aggregates = s.AggregateTypes.Select(t => t.Name).ToList();
        SpecAssert.Fact(!aggregates.Contains(type), $"{slice}'s aggregates are [{string.Join(", ", aggregates)}]; '{type}' should not be one.");
    }

    /// <summary>
    /// bobcat#502: a stream a spec arranges with <c>GivenContext</c> is one the slice reads before deciding --
    /// its <c>ReadsFrom</c> -- and not one of its aggregates. Read off the generated source directly, before the
    /// merge.
    /// </summary>
    [Then("this assembly's specs alone say the {slice} slice reads from {type} and does not decide against it")]
    public void TheSpecsAloneSayTheSliceReadsFrom(string slice, string type)
    {
        var declared = BobcatEventModelSource.Describe().Slices.FirstOrDefault(s => s.Name == slice);
        if (declared is null)
        {
            SpecAssert.Fail($"The specs declare no slice named '{slice}'.");
            return;
        }

        var reads = declared.ReadsFrom.Select(t => t.Name).ToList();
        SpecAssert.Fact(reads.Contains(type), $"The specs say {slice} reads from [{string.Join(", ", reads)}], expected '{type}'.");

        var aggregates = declared.AggregateTypes.Select(t => t.Name).ToList();
        SpecAssert.Fact(!aggregates.Contains(type),
            $"The specs say {type} is an aggregate of {slice} ([{string.Join(", ", aggregates)}]); a lookup stream is read from, not decided against.");
    }

    /// <summary>
    /// bobcat#500: the trigger this assembly's specs alone give a slice, before the merge -- so a route the
    /// generator resolved from a route name is checked as the specs' own claim, not as whatever Wolverine derived.
    /// </summary>
    [Then("this assembly's specs alone say the {slice} slice is triggered by {label}")]
    public void TheSpecsAloneSayTheSliceIsTriggeredAt(string slice, string label)
    {
        var declared = BobcatEventModelSource.Describe().Slices.FirstOrDefault(s => s.Name == slice);
        if (declared is null)
        {
            SpecAssert.Fail($"The specs declare no slice named '{slice}'.");
            return;
        }

        SpecAssert.Check("trigger", declared.TriggerOrigin?.Label, label);
    }

    /// <summary>
    /// The cross-slice join computed upstream on read (jasperfx#823): nobody declares a link, it falls out of
    /// one slice's EmittedEvents meeting another's ConsumedEvents.
    /// </summary>
    [Then("there is an {kind} link from the {from} slice to the {to} slice via {via}")]
    public void ThereIsALink(EventModelLinkKind kind, string from, string to, string via)
    {
        if (model() is not { } m) return;
        var links = m.Links;
        SpecAssert.Fact(links.Any(l => l.Kind == kind && l.FromSlice == from && l.ToSlice == to && l.Via.Name == via),
            $"No {kind} link {from} -> {to} via {via}. Links: " +
            string.Join(" | ", links.Select(l => $"{l.Kind} {l.FromSlice} -> {l.ToSlice} via {l.Via.Name}")));
    }

    [Then("the {slice} slice binds the specification {identity}")]
    public void TheSliceBindsTheSpecification(string slice, string identity)
    {
        if (sliceNamed(slice) is not { } s) return;
        var specs = s.Specifications;
        SpecAssert.Fact(specs.Any(x => x.Identity == identity),
            $"{slice} binds [{string.Join(", ", specs.Select(x => x.Identity))}], expected '{identity}'.");
    }

    private static string describeHotspots(EventModelSliceDescriptor slice)
        => string.Join(" | ", slice.Hotspots.Select(h => $"{h.Origin}: {h.Text}"));
}

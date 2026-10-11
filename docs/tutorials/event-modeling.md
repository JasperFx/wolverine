# Event Modeling with the Critter Stack

::: info
This is the one place where the whole Event Modeling workflow across the "Critter Stack" is written down end to end.
Each tool in the stack still has its own reference pages for its own API surface, and this tutorial links out to those
pages rather than repeating them. If you're looking for every flag on a command, follow the links.
:::

Let's say your team just came out of an [Event Modeling](https://eventmodeling.org) workshop with a board full of
commands, events, read models and the little Given/When/Then examples under each slice. That board is genuinely
valuable, but if you've ever done this before, you know what usually happens next. The board goes on a wiki page,
the developers start building, and within a couple sprints the diagram and the code no longer agree with each other
and nobody can say exactly where.

<!-- JEREMY: The opening problem statement (boards rotting on a wiki) is my guess at your framing; author it in your own words. -->

The Critter Stack approach is to treat the Event Model as something you *execute* rather than a picture you keep
next to the code. In practice that means:

1. The model is declared in plain C#, right beside the code that implements it
2. The board's examples become executable specifications that are red until the behavior exists
3. Wolverine writes the handler skeletons for every slice that doesn't have code yet
4. Wolverine reads the model back out of your real handlers, endpoints and projections, so any drift between what
   was designed and what was built shows up as an open question instead of going unnoticed

This tutorial walks through that whole loop in order, using one running example all the way through.

## The Pieces

Event Modeling support cuts across most of the Critter Stack, so it's worth knowing up front which tool does what:

| Tool | What it contributes |
|------|---------------------|
| [JasperFx.Events](https://shared-libs.jasperfx.net/event-modeling/declaring) | `EventModelDefinition` and the fluent `EventModelBuilder` for declaring a model in code, the provenance rules for merging several sources into one model, and the source generators |
| Wolverine | The model *derived* from your message handlers, HTTP endpoints and gRPC services, the `event-model`, `describe-event-model` and `scaffold` commands, and the `[Emits]` and `[SlicePattern]` attributes |
| Marten, Polecat and Fisher | A *derived* View slice for every projection registered in the event store, with no extra configuration |
| [Bobcat](https://bobcat.jasperfx.net) | `bobcat import-event-model` to turn an [eventmodelers.ai](https://eventmodelers.ai) board into C#, and the generated specifications (WolverineFx.Bobcat) that bind back to their slices |
| [Stoat](https://stoat.jasperfx.net) | `stoat plan-from-model` to turn a model into a coordination plan, and the agents that work through that plan slice by slice |
| [CritterWatch](https://critterwatch.jasperfx.net/) | Renders the same Event Model live from a running service |

::: tip
Every one of these tools speaks the same `EventModelDescriptor` shape from JasperFx, so a model declared in code, one
derived from Wolverine, and one observed by CritterWatch can all be merged into a single picture. That shared contract
is really the whole trick here.
:::

<!-- JEREMY: "the whole trick" is my characterization of why the pieces compose; reword or cut if it isn't how you'd sell it. -->

## The Running Example

Throughout this tutorial we'll use CritterCrush, a dog adoption and shelter application that grew out of a real Event
Modeling board. We're only going to work with two chapters of that board:

* **BookingAppointments** -- proposing, confirming, rescheduling, completing and cancelling appointments at the
  shelter. Home checks, foster handovers and surrender intakes all propose an appointment.
* **VolunteeringAndHomeChecks** -- people applying to volunteer, the shelter reviewing and approving those
  applications, and approved volunteers accepting and reporting on home checks.

On the board itself, the BookingAppointments chapter starts out looking like this in the
[emlang](https://eventmodelers.ai) export, with each `t`, `c`, `e` and `v` being a trigger, command, event and view, and
the examples under `tests`:

```yaml
BookingAppointments:
  steps:
  - c: System/Propose Home Check Appointment
    props:
      triggeredBy: VolunteeringAndHomeChecks (Accept Home Check Assignment)
  - e: System/Home Check Appointment Proposed
  - v: Shelter Staff/Appointments Queue
  - t: Shelter Staff/Confirm Appointment
  - c: Shelter Staff/Confirm Appointment
  - e: Shelter Staff/Appointment Confirmed
  # ...and several more
  tests:
    AppointmentConfirmed:
      given:
      - e: System/Home Check Appointment Proposed
      when:
      - c: Shelter Staff/Confirm Appointment
      then:
      - e: Shelter Staff/Appointment Confirmed
```

The application itself is a garden variety Wolverine + Marten application. The only thing about `Program` that matters
for this tutorial is that it uses `RunJasperFxCommands()` so that the command line tools in the rest of this page are
available, and that it names the service:

```csharp
builder.Host.UseWolverine(opts =>
{
    // This is also the name of the application's Event Model
    opts.ServiceName = "CritterCrush";
    opts.Policies.AutoApplyTransactions();
});

var app = builder.Build();
app.MapWolverineEndpoints();
return await app.RunJasperFxCommands(args);
```

## Declaring the Model in Code

Before we import anything, let's talk about what the model looks like in code, because that's where everything else
in this tutorial ends up. There is no YAML or JSON format to author anymore. You declare an Event Model by subclassing
`EventModelDefinition` from JasperFx.Events, and the usual pattern is one definition per chapter.

Here's the BookingAppointments chapter, trimmed down to a few slices:

```csharp
using JasperFx.Events.EventModeling;

namespace CritterCrush.BookingAppointments;

public class BookingAppointmentsModel : EventModelDefinition
{
    public override void Configure(EventModelBuilder model)
    {
        model.InChapter("BookingAppointments");

        // Every command slice opened after this decides against
        // the Appointment aggregate unless it says otherwise
        model.ForAggregate<Appointment>();

        // The system reacting to a home check assignment in the other chapter
        model.Automation("ProposeHomeCheckAppointment")
            .TriggeredBy("VolunteeringAndHomeChecks (Accept Home Check Assignment)", TriggerKind.MessageHandler)
            .Command<ProposeHomeCheckAppointment>()
            .StartsStream<Appointment>()
            .Emits<HomeCheckAppointmentProposed>();

        model.View<AppointmentsQueue>()
            .On<HomeCheckAppointmentProposed>()
            .On<FosterHandoverAppointmentProposed>()
            .On<SurrenderIntakeAppointmentProposed>();

        model.Command<ConfirmAppointment>()
            .TriggeredBy("Confirm Appointment", TriggerKind.Human)
            .Emits<AppointmentConfirmed>();

        model.Command<CancelAppointment>()
            .TriggeredBy("Cancel Appointment", TriggerKind.Human)
            .Emits<AppointmentCancelled>();
    }
}
```

Every type in there, `ConfirmAppointment`, `AppointmentConfirmed`, `Appointment` and so on, is a real C# type. In the
very beginning those are just field-less stubs like `public record AppointmentConfirmed;`, and that's perfectly fine.
A stub is something the compiler, your specifications and Wolverine's derived model all understand, and a typo in a
stub's name is a compile error rather than a mysterious second box on a diagram.

::: tip
Every role also accepts a plain name like `.Emits("AppointmentConfirmed")` for when you aren't ready to even stub a
type yet. Switch to a stub as soon as you can though.
:::

### Slices and Patterns

Each slice is opened with one of the pattern verbs on the builder:

* `Command<T>()` for a slice that changes state by appending events
* `Automation(name)` with `On<TEvent>()` for the system reacting to an event, or `Automation<TCommand>()` for a command
  fired by Wolverine's scheduled messages
* `View<T>()` for a read model that folds events
* `Translation(name)` for a slice whose other end is a system outside of yours

The type-first verbs name the slice exactly the way Wolverine names the slice it derives from your handler or HTTP
endpoint, so the declared slice and the derived slice line up on their own later. That's important, and we'll come
back to it at the end.

`InChapter()` and `InDomain()` on the builder are running defaults for every slice opened after them, and any single
slice can override either one.

### Deciding Against an Aggregate, or Starting a Stream

For command slices, the model needs to say *where* the decision is made, because that's very different code:

* `Against<T>()` says the slice decides against an existing aggregate's stream, which is going to turn into a
  `[WriteModel]` parameter in a Wolverine handler
* `StartsStream<T>()` says the slice starts a brand new stream, which is going to turn into a `StartStream` return value
* `NoAggregate()` says the slice deliberately decides against nothing
* `ForAggregate<T>()` on the builder sets a default aggregate for every *command* slice in that definition that
  doesn't use any of the above

A single command can also decide against more than one stream. In the VolunteeringAndHomeChecks chapter, accepting a
home check assignment has to look at both the home check and the volunteer's application:

```csharp
model.Command<AcceptHomeCheckAssignment>()
    .TriggeredBy("Home Check Assigned", TriggerKind.Human)
    .Against<HomeCheck>()
    .Against<VolunteerApplication>()
    .Emits<HomeCheckAssignmentAccepted>();
```

### Views

A View slice is projected by Marten, Polecat or Fisher, and the model can say how. `AsSingleStream()` is the default, and
it means one read model per stream. `AsMultiStream()` is one read model per identity that events from many streams are grouped into,
and `AsDcbModel()` folds the events a Dynamic Consistency Boundary tag query selects. Our appointments queue spans every
appointment at the shelter, so it's really a multi-stream projection:

```csharp
model.View<AppointmentsQueue>()
    .AsMultiStream()
    .On<HomeCheckAppointmentProposed>()
    .On<FosterHandoverAppointmentProposed>()
    .On<SurrenderIntakeAppointmentProposed>();
```

That doesn't change the picture of the model at all, but it does change what `wolverine scaffold` writes for you later.

### Dynamic Consistency Boundaries

Going back to `AcceptHomeCheckAssignment` for a second, deciding against two streams at once is exactly the kind of
decision where you might reach for a [Dynamic Consistency Boundary](/guide/durability/marten/event-sourcing#dynamic-consistency-boundary-dcb)
instead of two aggregates. If so, say so with `DcbModel<T>()`, which is just another name for `DeciderModel<T>()`:

```csharp
model.Command<AcceptHomeCheckAssignment>()
    .TriggeredBy("Home Check Assigned", TriggerKind.Human)
    .DcbModel<HomeCheckAssignment>()
    .Emits<HomeCheckAssignmentAccepted>();
```

<!-- JEREMY: Is AcceptHomeCheckAssignment a case you would actually steer toward a DCB decider, or do you prefer the two [WriteAggregate] streams for CritterCrush? I used it only because Bobcat flags it as "several-streams". -->

### Domains for Modular Monoliths

In a modular monolith, a module is a *domain*. The BookingAppointments chapter really belongs to a Scheduling module,
so we could say that once at the top of the definition:

```csharp
model.InDomain("Scheduling").InChapter("BookingAppointments");
```

You can also declare domains as policies, like `model.Domain("Scheduling").IncludesNamespace("CritterCrush.BookingAppointments")`,
or with `[Domain("Scheduling")]` directly on a handler or endpoint. Domain membership is *declared, never inferred*. Nothing
in the Critter Stack will guess a module from your namespaces or assembly names on its own. See
[One message, several modules](/guide/command-line#one-message-several-modules) for how Wolverine uses domains when the
same message is handled by more than one module.

### Registering the Model

Here's the nice part. You don't have to register anything. Wolverine finds every `EventModelDefinition` in your
application from a manifest the JasperFx source generator writes at compile time, and a definition that doesn't
override `Name` joins the application's model, which is named after `opts.ServiceName`. If you'd rather be explicit, you
can still use `services.AddEventModel<BookingAppointmentsModel>()`, and `opts.AutoRegisterEventModelDefinitions = false`
turns the discovery off.

::: tip
The full builder API, including hotspots, external systems, explicit specification links and declared aggregates, is in
[Declaring the Model in Code](https://shared-libs.jasperfx.net/event-modeling/declaring) in the JasperFx documentation.
:::

## Importing a Board with Bobcat

You can certainly write those definitions by hand, but if your model already lives on an
[eventmodelers.ai](https://eventmodelers.ai) board, Bobcat will write them for you. Install the tool:

```bash
dotnet tool install -g Bobcat.Console
```

Then point it at the board export and the application project. For CritterCrush, we only want two of the board's
chapters, and we want the specifications too:

```bash
bobcat import-event-model K9CRUSH.emlang.v3.yaml \
  --model CritterCrush --namespace CritterCrush \
  --out ./CritterCrush \
  --chapter BookingAppointments VolunteeringAndHomeChecks \
  --specs --store marten
```

::: warning
`--model` is the *merge key*. It has to match what Wolverine calls the model, which is `opts.ServiceName`, or the import
floats off as a second, separate diagram.
:::

### What It Writes

The import writes ordinary C# into a folder per chapter:

```
CritterCrush/
  Features/
    BookingAppointments/
      BookingAppointmentsModel.cs     the chapter's EventModelDefinition
      ConfirmAppointment.cs           the command and the events it emits
      Appointment.cs                  the aggregate, since no slice "produces" it
      AppointmentsQueue.cs            the view's read model
      ...
    VolunteeringAndHomeChecks/
      VolunteeringAndHomeChecksModel.cs
      ...
```

Each slice goes in `Features/{Chapter}/{Slice}.cs` in the namespace `{Root}.{Chapter}`, so `ConfirmAppointment` lands in
`CritterCrush.BookingAppointments`. The slice's file holds what the slice produces:

```csharp
namespace CritterCrush.BookingAppointments;

/// <summary>
/// the command of ConfirmAppointment.
/// </summary>
public record ConfirmAppointment(Guid Id);

/// <summary>
/// emitted by ConfirmAppointment.
/// </summary>
public record AppointmentConfirmed;
```

A stub only has what the board names, plus an `Id` on a command or aggregate where the board doesn't name an identity,
because that's Wolverine's own convention for finding the stream. Events stay field-less. That isn't a gap in the
importer, it's honestly all a board export can tell it. The fields are yours to write as the behavior takes shape.

**Nothing regenerates these files.** The import never overwrites a file it already wrote, so your edits are safe, and
running it again only fills in what's missing.

### Correcting the Guesses

An Event Modeling board doesn't actually say which stream an event goes into, so Bobcat *infers* the aggregates from
the examples on the board, and it's upfront about that. Every inferred aggregate is reported, and is also left as a
comment right on the slice in the generated definition:

```csharp
// ⚠ inferred: decides against Appointment -- its examples give Appointment events before it appends...
model.Command<ConfirmAppointment>()
    .TriggeredBy("Confirm Appointment", TriggerKind.Human)
    .Emits<AppointmentConfirmed>();
```

The segmentation is a set of reported guesses, so a wrong guess is meant to be a one line edit in that generated C#
rather than a re-import. If you'd rather settle it on the import itself, there are flags for that, and they always win
over the inference:

| Flag | What it settles |
|------|-----------------|
| `--aggregate Slice=Type` | The aggregate a slice decides against, as in `--aggregate ConfirmAppointment=Appointment AcceptHomeCheckAssignment=HomeCheck` |
| `--starts-stream Slice=Type` | That a slice *starts* a stream, as in `--starts-stream RequestHomeCheck=HomeCheck` |
| `--chapter A B` | Import only these chapters. The rest of the board is still read as evidence |
| `--json` | Write everything to stdout as JSON for tooling, with a stable `code` on every callout |

`--chapter` deserves a little more explanation. When we import only BookingAppointments and VolunteeringAndHomeChecks,
an example whose earlier events come from a chapter we left out can't prove that the stream already exists. Bobcat
doesn't guess in that case. It asks:

```
⚠ slice 'RequestHomeCheck' starts the HomeCheck stream? It is inferred to decide against HomeCheck,
but the only HomeCheck events its examples give first, ApplicationReviewed, come from chapter
'ShelterReviewsApplication', which was not imported. If it starts the stream, import with
--starts-stream RequestHomeCheck=HomeCheck.
```

With `--json`, every one of those callouts carries a stable code like `inferred-against`, `inferred-stream-start`,
`stream-start-from-unimported-chapter` or `several-streams`, the evidence it was drawn from, and the exact flag that
settles it. That's what makes the import usable from an AI agent or from Stoat. See
[The `bobcat` Tool](https://bobcat.jasperfx.net/bobcat-tool#import-event-model) for the full list of codes and options.

## Generated Specifications

With `--specs`, the import also writes one [WolverineFx.Bobcat](https://bobcat.jasperfx.net/integrations/wolverine)
specification per example on the board into a sibling `CritterCrush.Specs` project, with a folder per chapter. The
`AppointmentConfirmed` example from the board becomes this:

```csharp
namespace CritterCrush.Specs.BookingAppointments;

[BobcatFeature("ConfirmAppointment")]
[BobcatSlice(SliceType = typeof(ConfirmAppointment))]
public class confirm_appointment(AppFixture app) : CritterCrushSpec(app)
{
    [Fact]
    public async Task appointment_confirmed()
    {
        var theAppointment = Guid.CreateVersion7();
        await GivenEvents<Appointment>(theAppointment, Specify<HomeCheckAppointmentProposed>());

        await WhenReceived(Specify<ConfirmAppointment>().With(x => x.Id, theAppointment));

        ThenEvents(Specify<AppointmentConfirmed>());
    }
}
```

And the one for accepting a home check assignment arranges and checks both of its streams:

```csharp
[Fact]
public async Task home_check_assignment_accepted()
{
    var theHomeCheck = Guid.CreateVersion7();
    var theVolunteerApplication = Guid.CreateVersion7();
    await GivenEvents<HomeCheck>(theHomeCheck, Specify<HomeCheckRequested>());
    await GivenEventsOn<VolunteerApplication>(theVolunteerApplication, Specify<VolunteerApproved>());

    await WhenReceived(Specify<AcceptHomeCheckAssignment>()
        .With(x => x.HomeCheckId, theHomeCheck)
        .With(x => x.VolunteerApplicationId, theVolunteerApplication));

    ThenEventsOn<HomeCheck>(theHomeCheck, Specify<HomeCheckAssignmentAccepted>());
    ThenNoEventsOn<VolunteerApplication>(theVolunteerApplication);
}
```

These are ordinary xUnit v3 tests running against the application's real host through [Alba](https://jasperfx.github.io/alba),
and the base class resets the event store before each one. Right now, every one of them is red because there's no handler
behind any of these commands yet. **That is the point.** The specifications say what "done" means for each slice before
anybody writes a line of the implementation.

::: tip
Every id a generated spec mints is `Guid.CreateVersion7()` rather than `Guid.NewGuid()`. Generated code gets copied
around, and random v4 Guids as stream ids fragment the event store's indexes.
:::

### How a Specification Binds to Its Slice

There are two links between a specification and the model, and both are generated for you:

1. `[BobcatSlice(SliceType = typeof(ConfirmAppointment))]` on the spec class binds the class to its slice, which is also
   how your IDE gets from a spec to the slice it specifies
2. The definition links the slice to the identity the spec reports when it runs, which is the feature name and then
   the test method's name read back as a sentence:

```csharp
model.Command<ConfirmAppointment>()
    .TriggeredBy("Confirm Appointment", TriggerKind.Human)
    .Emits<AppointmentConfirmed>()
    .LinksToSpecification("ConfirmAppointment/appointment confirmed");
```

If those identities ever drift apart, a slice can look "unproven" with every one of its specs green, so Bobcat has a
[gate that checks spec identities against the model](https://bobcat.jasperfx.net/spec-identities#checking-identities-against-the-model)
that you'll want in your build.

### Expected Objects, and Why Member Checks Want C# 14

When a specification needs to say what an event, aggregate or read model should look like, Bobcat gives you three ways
to write the expected object. The first two read the same on every version of C#. The examples below assume you've
given the CritterCrush types some real fields by now, since the generated stubs start out with none.

The `.With()` chain, which is what the generator writes when an object has three or fewer specified members:

```csharp
await ThenAggregate<Appointment>(theAppointment, Specify<Appointment>()
    .With(x => x.Kind, AppointmentKind.HomeCheck));
```

A `Property | Value` table, which the generator writes when there are more members than that:

```csharp
await WhenReceived(Specify<RescheduleAppointment>($$"""
    | Property     | Value                |
    | Id           | {{theAppointment}}   |
    | ScheduledFor | 2026-11-02T15:00:00Z |
    | Reason       | Volunteer is sick    |
    """));
```

And *member checks*, which are assertions on the members for anything that isn't plain equality:

```csharp
await ThenReadModel<VolunteerApplicationsQueue>(theQueue, Specify<VolunteerApplicationsQueue>(
    x => x.Pending.ShouldBe(1),
    x => x.Oldest.ShouldBeLessThan(DateTimeOffset.UtcNow)));
```

`.Check(...)` adds the same kind of member checks to a `.With()` chain. Bobcat reads each check as an expression tree, so
it can show the member and the expected value in the specification's report just like it does for `.With()`.

That's also the catch. Before C# 14, an expression tree can't leave out an optional argument, and every Shouldly
assertion has one, its `customMessage`. On net9.0, or anywhere `LangVersion` is pinned below 14, the member checks above
fail to compile with:

```
error CS0854: An expression tree may not contain a call or invocation that uses optional arguments
```

The fix is to spell out every optional argument, positionally:

| Language version | What compiles |
|------------------|---------------|
| C# 14, the net10.0 default | `x => x.Pending.ShouldBe(1)` |
| C# 12 or 13 (net9.0, or a pinned `LangVersion`) | `x => x.Pending.ShouldBe(1, null)` |

::: tip
If you're on C# 12 or 13, our advice is to use the `.With()` chain or the table for anything that's plain equality, and
only reach for member checks, with the optional arguments spelled out, for the checks that aren't. Wolverine's own test
suite builds with C# 12, which is why you'll see `ShouldBeGreaterThan(3, null)` in its WolverineFx.Bobcat tests.
:::

<!-- JEREMY: bobcat#452 says the expected-object forms should get their own Bobcat page; today the only Bobcat home is the import-event-model section of bobcat-tool.md. Swap in the dedicated page's link here once it exists. -->

## Scaffolding the Handlers

We have a model and a pile of red specifications, so now we need the actual code. This is where Wolverine's `scaffold`
command comes in. It builds your application's host *without starting it*, assembles the declared model together with
everything Wolverine derives from your real handlers and endpoints, and writes an implementation skeleton for every slice
that's declared but has no code behind it yet.

Always look first:

```bash
dotnet run -- scaffold --dry-run
```

And then let it write:

```bash
dotnet run -- scaffold
```

The scaffold uses the same `Features/{Chapter}/` layout and `{Root}.{Chapter}` namespaces that the Bobcat import writes,
so a handler lands right next to its command. For `ConfirmAppointment`, the handler is added to the end of
`Features/BookingAppointments/ConfirmAppointment.cs`:

```csharp
// Scaffolded by `wolverine scaffold` from the declared Event Model slice 'ConfirmAppointment'.
// It is yours now: the scaffold never writes to this class again.
public static class ConfirmAppointmentHandler
{
    public static AppointmentConfirmed? Handle(ConfirmAppointment command, [WriteModel] Appointment appointment)
    {
        // Fill this in and delete the throw -- the shape is:
        //     return new AppointmentConfirmed(...);   // or null when there is nothing to record
        // If the stream may not exist yet, make the parameter Appointment?
        // -- [WriteModel] then does not require it
        throw new NotImplementedException("TODO: ConfirmAppointment");
    }
}
```

A few things to note about what the scaffold does:

* The skeletons are always in Wolverine's store-agnostic style using `[WriteModel]`, `StartStream`, `EventsToAppend` and
  `IStorageAction<T>`, so the same code runs on Marten, Polecat, Fisher or the in-memory prototyping store
* Method bodies throw `NotImplementedException`, so a slice nobody has filled in yet never stops the rest of the
  application from compiling
* An existing aggregate or view gets the `Apply` methods it's missing added to the end of its class
* A view declared with `AsMultiStream()` gets its projection class, plus a note telling you to register it with an
  `Async` lifecycle
* A slice triggered by `TriggerKind.Human` becomes a message handler that your UI sends to. Declare
  `TriggeredBy(TriggerKind.Http)` instead if you want a Wolverine.HTTP endpoint
* **It never overwrites a file.** An implemented slice isn't declared-only anymore, so running the scaffold a second time
  is a no-op

The report is one line per slice, written so that either a person or an agent can act on it. If a slice's trigger can't
be determined from either the declaration or the code, the scaffold reports it as `UNKNOWN` rather than guessing, and
tells you exactly what to declare.

For tooling, `--json` writes the whole plan to stdout instead: every file with its mode (`write`, `append` or `insert`),
and every notice with a stable `code` such as `file-written`, `handler-appended`, `apply-methods-added`,
`unknown-trigger` or `no-aggregate`. Like Bobcat's import, codes are only ever added and never renamed.

From here, the work is just the normal Critter Stack development loop. Fill in the handler, give the events and the
aggregate their fields, and run the specifications until they're green. See
[Scaffolding Declared Slices](/guide/command-line#scaffolding-declared-slices) for the full table of what each kind of
declared slice turns into.

::: tip
The [Critter Stack AI Skills](https://ai-skills.jasperfx.net) include skills for exactly this step -- taking a red
specification and the scaffolded skeleton and writing the implementation in house style.
:::

<!-- JEREMY: Do you want the AI Skills plug here, and if so, should it name specific skills (critterstack-sdd-code-from-specs)? -->

## Planning the Work with Stoat

If you're handing slices to AI agents as well as people, [Stoat](https://stoat.jasperfx.net) can turn the
same board into a coordination plan. `stoat plan-from-model` runs Bobcat's own importer to write the same stubs,
definitions and specifications described above, then imports a plan with one node per slice, each bound to the
specification identities that prove it:

```bash
stoat plan-from-model K9CRUSH.emlang.v3.yaml ./CritterCrush \
  --plan crittercrush --model CritterCrush --namespace CritterCrush \
  --chapter BookingAppointments VolunteeringAndHomeChecks
```

Dependencies only come from what the model states. An automation like `ProposeHomeCheckAppointment` depends on the slice
its trigger names, while command and view slices arrange their own events in their specifications and can be built in
parallel. `stoat run-plan` then works through the ready slices, and a slice is only done when its specifications are
green. See [From an event model to a plan](https://stoat.jasperfx.net/guide/plan-from-model) and
[The chapter runner](https://stoat.jasperfx.net/guide/run-plan) for the details.

<!-- JEREMY: Stoat is commercial. Do you want a sentence here about licensing, like the blog draft had, or leave it out? -->

## Checking the Model Against the Code

This is the part that keeps the model from rotting. Wolverine *derives* its own view of the Event Model from your code:
the command each handler or endpoint takes, the aggregate it decides against, the events it returns, the messages it
cascades, the read models it loads and produces, and how it's triggered. Marten, Polecat and Fisher do the same for every
registered projection. All of that is merged with your declared definitions into one model, and you can read it from the
command line:

```bash
dotnet run -- describe-event-model
dotnet run -- describe-event-model --chapter BookingAppointments
dotnet run -- describe-event-model --hotspots-only > open-questions.txt
```

In a terminal you get a tree by domain and chapter. Each slice shows its pattern, its trigger, the aggregate and *why* it
has that aggregate, the events it emits, the code that handles it, its specifications and its hotspots, with a summary table of implemented and
specified slices per chapter at the bottom. Redirect it to a file and you get plain text you can paste into a GitHub
issue or a pull request.

### Declared versus Derived

Every claim in the merged model carries its *provenance*, and the slice headings in `describe-event-model` show it:

* **Declared** -- somebody wrote it down in an `EventModelDefinition`
* **Derived** -- Wolverine or the event store read it out of the code

When both describe the same slice, the code wins, because the code is what actually ships. If they agree, nothing
happens at all. If they *don't* agree -- say the definition says `ConfirmAppointment` emits `AppointmentConfirmed` but
the handler returns something else -- the merge keeps the code's answer and records yours as a `SourceDisagreement`
hotspot on the slice. Slice names, trigger labels, chapters and domains only ever come from declarations, so those
stay yours.

That gives you a very practical to-do list while you build. Early on, every slice is declared and grey. As you fill in
handlers, slices pick up derived claims, and any hotspot is either a design question somebody needs to answer or a
place where the code wandered away from the design.

::: warning
Wolverine can only derive the events a handler emits from what it can see. A handler that returns `EventsToAppend` or a
`StartStream` builds its events in the method body, and the JasperFx.Events source generator reads them from there. If
the events are built somewhere the generator can't see, like a helper method, put `[Emits(typeof(AppointmentConfirmed))]`
on the handler. Otherwise the derived list is simply empty, and an empty list can't contradict a declaration, so the drift
goes unseen. See [Naming the events a signature cannot carry](/guide/command-line#naming-the-events-a-signature-cannot-carry).
:::

::: tip
A message handler's slice pattern is the one thing Wolverine often can't tell from the code, because the same handler
signature could be a person asking (a `Command`) or the system reacting (an `Automation`). If a slice shows up without a
pattern, `[SlicePattern(SlicePattern.Command)]` on the handler fills the gap.
:::

### Seeing the Model

`describe-event-model` is for reading. To *look* at the model, the `event-model` command writes the same merged model
as JSON, or pushes it straight to a monitor, without needing a running system:

```bash
dotnet run -- event-model --json ./event-model.json
dotnet watch run -- event-model --url http://localhost:5525/api/event-model
```

`--url` is just an HTTP PUT of the model to whatever URL you name, so Wolverine takes no dependency on the monitor. The
second form, under `dotnet watch`, republishes the model every time you recompile, which makes for a very tight design
loop on Stoat's Event Model canvas. Once the application is running for real, [CritterWatch](https://critterwatch.jasperfx.net/)
renders the same model live from the running service. See [Exporting the Event Model](/guide/command-line#exporting-the-event-model)
for everything else the export can do.

<!-- JEREMY: I used Stoat's own docs for the URL (PUT to :5525/api/event-model, canvas at :5526/event-model); command-line.md shows a bare http://localhost:5525. Is naming Stoat as the --url target what you want in the Wolverine docs? -->

## Summary

To recap the whole loop with CritterCrush:

1. `bobcat import-event-model` turned two chapters of the board into C# stubs, one `EventModelDefinition` per chapter
   under `Features/{Chapter}/`, and a red specification per example
2. We corrected Bobcat's reported guesses with one line edits or with `--aggregate` and `--starts-stream`
3. `wolverine scaffold` wrote a store-agnostic handler skeleton next to every command with no code behind it
4. We filled in the handlers until the specifications went green
5. `describe-event-model` showed us what's implemented, what's specified, and where the code and the model disagree

Hopefully that gives you a feel for how the pieces fit together. For the details of any one step, the reference pages are:

| Topic | Reference |
|-------|-----------|
| The builder API | [Declaring the Model in Code](https://shared-libs.jasperfx.net/event-modeling/declaring) (JasperFx) |
| Importing a board, generated specs, expected objects | [The `bobcat` Tool](https://bobcat.jasperfx.net/bobcat-tool#import-event-model) (Bobcat) |
| Spec identities and the gate | [Spec Identities](https://bobcat.jasperfx.net/spec-identities) (Bobcat) |
| `event-model`, `describe-event-model`, `[Emits]`, `[SlicePattern]`, domains | [Exporting the Event Model](/guide/command-line#exporting-the-event-model) |
| `wolverine scaffold` | [Scaffolding Declared Slices](/guide/command-line#scaffolding-declared-slices) |
| Plans and agents | [From an event model to a plan](https://stoat.jasperfx.net/guide/plan-from-model) (Stoat) |

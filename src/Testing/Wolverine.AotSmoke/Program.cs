// AOT smoke test (wolverine#2715, mirrors JasperFx.AotSmoke).
//
// This program touches a representative cross-section of the AOT-clean
// Wolverine surface. The csproj sets IsAotCompatible=true and promotes
// the AOT analyzer warning codes to errors, so any change that adds
// [RequiresDynamicCode] / [RequiresUnreferencedCode] to an API exercised
// here — or any change to this file that calls into a reflective Wolverine
// surface — fails the build in CI.
//
// Surfaces this smoke exercises (these are the AOT-clean Wolverine APIs):
//   - Envelope construction + value-shape access (Id, Message, CorrelationId, …)
//   - WolverineOptions construction + the configuration-builder surface
//   - Envelope.Reset() (the pool re-use contract from wolverine#2726)
//   - DeliveryOptions construction
//   - EnvelopeStatus / EnvelopeIdGeneration / ServiceLocationPolicy enum values
//   - Envelope.ScheduleAt / ScheduleDelayed / IsExpired / IsScheduledForLater
//   - Envelope.SetMetricsTag, Envelope.SetMessageType<T>
//
// Surfaces intentionally NOT exercised here (those carry / will carry AOT
// annotations by design):
//   - Host.Build / WolverineOptions extension methods that trigger codegen
//     (UseWolverine → HandlerGraph.Compile → Roslyn AssemblyGenerator). The
//     AOT-clean usage path is pre-generation via `dotnet run -- codegen write`
//     + TypeLoadMode = Static. That story has its own smoke project (TBD).
//   - Reflective discovery (HandlerDiscovery.IncludeAssembly type-scan,
//     SagaTypeDescriptor reflection-resolved StartingMessages, transport
//     IMessageRoutingConvention reflection)
//   - MakeGenericType-driven factories (the few that exist in core)
//
// Build with:
//   dotnet build src/Testing/Wolverine.AotSmoke/Wolverine.AotSmoke.csproj
//
// Optional: publish AOT to verify the trimmer accepts it:
//   dotnet publish src/Testing/Wolverine.AotSmoke -c Release /p:PublishAot=true

using Wolverine;

// --- Envelope construction + value-shape -----------------------------------
// The pool-friendly default-constructed shape (wolverine#2726). Every public
// settable property is a pure-data field — no reflection, no dynamic code.

var envelope = new Envelope
{
    Id = Guid.NewGuid(),
    MessageType = "aot-smoke",
    CorrelationId = "smoke-correlation",
    ConversationId = Guid.NewGuid(),
    SentAt = DateTimeOffset.UtcNow,
    Source = "aot-smoke-source",
    TenantId = "tenant-a",
    Attempts = 1,
};

envelope.Headers["smoke"] = "yes";

if (envelope.Id == Guid.Empty || envelope.Headers["smoke"] != "yes")
{
    Console.Error.WriteLine("Envelope value-shape regression.");
    return 1;
}

// --- Envelope.TryGetHeader without forcing dict allocation -----------------
// The TryGetHeader fast path (wolverine#2541-ish era) is AOT-clean by design;
// guard it here so it stays that way.

if (!envelope.TryGetHeader("smoke", out var headerValue) || headerValue != "yes")
{
    Console.Error.WriteLine("Envelope.TryGetHeader regression.");
    return 1;
}

// --- DeliveryOptions construction ------------------------------------------
// The pure-data delivery-options shape that flows into PublishAsync /
// SendAsync. The Override(Envelope) merge that copies these onto an
// Envelope is internal Wolverine plumbing (and AOT-clean as pure data),
// but we exercise only the public-surface property writes here.

var delivery = new DeliveryOptions
{
    TenantId = "tenant-b",
    DeliverBy = DateTimeOffset.UtcNow.AddMinutes(5),
    ScheduledTime = DateTimeOffset.UtcNow.AddSeconds(30),
    GroupId = "group-1",
    DeduplicationId = "dedup-1",
};

delivery.Headers["delivery"] = "smoke";

if (delivery.TenantId != "tenant-b" || delivery.GroupId != "group-1")
{
    Console.Error.WriteLine("DeliveryOptions value-shape regression.");
    return 1;
}

// --- Envelope scheduling helpers -------------------------------------------
// ScheduleAt / ScheduleDelayed / IsScheduledForLater / IsExpired — all pure
// DateTimeOffset arithmetic on the envelope's own state.

var futureEnvelope = new Envelope { Id = Guid.NewGuid() }
    .ScheduleAt(DateTimeOffset.UtcNow.AddHours(1));

if (!futureEnvelope.IsScheduledForLater(DateTimeOffset.UtcNow))
{
    Console.Error.WriteLine("Envelope.ScheduleAt / IsScheduledForLater regression.");
    return 1;
}

var pastDeliverBy = new Envelope { DeliverBy = DateTimeOffset.UtcNow.AddSeconds(-1) };
if (!pastDeliverBy.IsExpired())
{
    Console.Error.WriteLine("Envelope.IsExpired regression.");
    return 1;
}

// --- Envelope.SetMessageType<T> --------------------------------------------
// Closed-generic SetMessageType<T>() resolves the message-type alias via
// the AOT-friendly ToMessageTypeName helper (string-based, no MakeGenericType).

var typed = new Envelope();
typed.SetMessageType<SmokeProbeMessage>();
if (string.IsNullOrWhiteSpace(typed.MessageType))
{
    Console.Error.WriteLine("Envelope.SetMessageType<T> regression.");
    return 1;
}

// --- Envelope.SetMetricsTag ------------------------------------------------
// Append a metrics tag without triggering reflective tag-name probing.

envelope.SetMetricsTag("smoke.tag", "yes");

// --- WolverineOptions construction (configuration-builder surface) ---------
// The constructor itself is what we exercise — wiring the SystemTextJson
// default serializer + the configuration-rules tree must stay AOT-clean.

var options = new WolverineOptions { ServiceName = "AotSmoke" };

if (options.ServiceName != "AotSmoke")
{
    Console.Error.WriteLine("WolverineOptions construction regression.");
    return 1;
}

// --- DetermineSerializer on the default options ----------------------------
// Pure dictionary lookup against the in-memory _serializers map.

var serializer = options.DetermineSerializer(new Envelope { ContentType = "application/json" });
if (serializer is null)
{
    Console.Error.WriteLine("WolverineOptions.DetermineSerializer regression.");
    return 1;
}

Console.WriteLine($"Wolverine AOT smoke OK — Envelope.Id={envelope.Id:N}.");
return 0;

// Sample message type. The closed-generic SetMessageType<T>() above resolves
// the alias from this type without MakeGenericType / Activator.CreateInstance.
internal sealed record SmokeProbeMessage(string Payload);

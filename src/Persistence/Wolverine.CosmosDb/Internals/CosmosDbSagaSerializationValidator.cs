using System.Reflection;
using System.Text;
using System.Text.Json;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;

namespace Wolverine.CosmosDb.Internals;

/// <summary>
/// GH-3416: refuses a CosmosClient that would not write a saga's identity member as the lowercase
/// <c>id</c> property CosmosDB demands on every document.
///
/// CosmosDB rejects any document without an <c>id</c> field. Wolverine's own envelope, node, and dead
/// letter documents carry explicit <c>[JsonProperty("id")]</c>/<c>[JsonPropertyName("id")]</c> mappings,
/// so message persistence works with any serializer — but a user's saga is their own POCO with a
/// PascalCase <c>Id</c>, and the CosmosClient's DEFAULT serializer writes that as <c>"Id"</c>. The first
/// saga persist then fails with "400 Bad Request: The input content is invalid because the required
/// properties - 'id; ' - are missing", and because the saga never landed, every follow-up message for it
/// dies with an <c>UnknownSagaException</c> that points nowhere near the real cause. The fix is a one-line
/// <c>CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase }</c> —
/// which is exactly what Wolverine's own Cosmos test fixture sets, and the only reason its saga suite is
/// green. Better to say so at startup than to let the user find it through a 400 in production.
///
/// Checked at host start rather than inside <c>UseCosmosDbPersistence()</c> because the CosmosClient is
/// pulled from DI — it does not exist while Wolverine is being configured, and the user may register it
/// either side of the UseWolverine() call. By start-up both the client and the handler graph are final.
/// The check only looks at saga types this Cosmos provider actually persists, so an application that uses
/// CosmosDB purely for envelope storage still boots.
/// </summary>
public class CosmosDbSagaSerializationValidator : IHostedService
{
    private readonly CosmosClient _client;
    private readonly IServiceContainer _container;
    private readonly HandlerGraph _handlers;
    private readonly WolverineOptions _options;

    public CosmosDbSagaSerializationValidator(CosmosClient client, HandlerGraph handlers, WolverineOptions options,
        IServiceContainer container)
    {
        _client = client;
        _handlers = handlers;
        _options = options;
        _container = container;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var clientOptions = _client.ClientOptions;

        // The SDK wraps even its own default serializer, so this is only ever null if the client was
        // built in some way we do not understand -- in which case there is nothing to assert
        var serializer = clientOptions.Serializer;
        if (serializer == null)
        {
            return Task.CompletedTask;
        }

        var offenders = cosmosSagaTypes()
            .Where(x => !CosmosSagaIdentity.WillWriteCosmosId(x, serializer))
            .ToArray();

        if (offenders.Length != 0)
        {
            throw new InvalidOperationException(CosmosSagaIdentity.ToFailureMessage(offenders, serializer,
                clientOptions.SerializerOptions?.PropertyNamingPolicy));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The saga state types that this Cosmos provider is the one persisting. Resolved through the same
    /// GetPersistenceProviders() lookup the saga code generation itself uses, so a mixed-persistence
    /// application is judged on the storage its sagas actually get, not on the packages it references.
    /// </summary>
    private IEnumerable<Type> cosmosSagaTypes()
    {
        var rules = _options.CodeGeneration;

        return _handlers.Chains
            .OfType<SagaChain>()
            .Where(chain => rules.GetPersistenceProviders(chain, _container) is CosmosDbPersistenceFrameProvider)
            .Select(chain => chain.SagaType)
            .Distinct();
    }
}

/// <summary>
/// Answers "what JSON property will this saga's identity member actually be written under?" by handing a
/// probe instance of the saga to the CosmosClient's own serializer and reading the JSON back. Asking the
/// serializer beats reasoning about it: naming policies, [JsonProperty]/[JsonPropertyName] mappings,
/// contract resolvers and custom CosmosSerializer implementations all get the same honest answer, and one
/// that cannot drift from what the client will really send to CosmosDB.
/// </summary>
public static class CosmosSagaIdentity
{
    /// <summary>
    ///     The document id property CosmosDB requires. Lowercase, always.
    /// </summary>
    public const string CosmosIdProperty = "id";

    // Written into the identity member so the property carrying it can be picked back out of the JSON,
    // whatever the serializer decided to call it
    private const string Probe = "wolverine-identity-probe";

    /// <summary>
    /// Will a document written for this saga carry the "id" CosmosDB requires? Anything we cannot work out
    /// -- a saga we cannot construct, a serializer that will not take it -- answers true: a startup guard
    /// that cries wolf on an application that works is worse than no guard at all.
    /// </summary>
    public static bool WillWriteCosmosId(Type sagaType, CosmosSerializer serializer)
    {
        return DetermineJsonPropertyName(sagaType, serializer) is null or CosmosIdProperty;
    }

    /// <summary>
    /// The JSON property name the saga's identity member is serialized under, or null when that cannot be
    /// determined.
    /// </summary>
    public static string? DetermineJsonPropertyName(Type sagaType, CosmosSerializer serializer)
    {
        var member = SagaChain.DetermineSagaIdMember(sagaType, sagaType);
        if (member == null)
        {
            return null;
        }

        var saga = tryStamp(sagaType, member, out var stamped);
        if (saga == null)
        {
            return null;
        }

        using var document = trySerialize(saga, serializer);
        if (document == null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (stamped)
        {
            // The property holding the probe IS the identity member, under whatever name it was written
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == Probe)
                {
                    return property.Name;
                }
            }
        }

        // The identity member would not take the probe (no setter, not a string). Fall back to asking
        // whether the document has an id at all
        return document.RootElement.TryGetProperty(CosmosIdProperty, out _) ? CosmosIdProperty : member.Name;
    }

    public static string ToFailureMessage(IReadOnlyList<Type> offenders, CosmosSerializer serializer,
        CosmosPropertyNamingPolicy? policy)
    {
        var message = new StringBuilder();

        message.Append(
            "CosmosDB rejects any document without a lowercase 'id' property, and the registered CosmosClient will not write one for ");
        message.Append(offenders.Count == 1 ? "this saga: " : "these sagas: ");
        message.Append(offenders.Select(x => describe(x, serializer)).Join(", "));
        message.Append(". ");

        message.Append(
            "The first saga persist would fail with \"400 Bad Request: The input content is invalid because the required properties - 'id; ' - are missing\", and every later message for that saga would then fail with an UnknownSagaException because the saga was never stored. ");

        if (policy != CosmosPropertyNamingPolicy.CamelCase)
        {
            message.Append(
                "Configure the CosmosClient to camel case its property names -- new CosmosClient(connectionString, new CosmosClientOptions { SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase } }) -- so that a saga's Id is written as 'id'. ");
        }

        message.Append(
            "Alternatively, map the saga's identity member onto the document id yourself with Newtonsoft's [JsonProperty(\"id\")] -- note that the CosmosDB SDK's default serializer is Newtonsoft based, so a System.Text.Json [JsonPropertyName(\"id\")] does nothing unless you also register a System.Text.Json based CosmosSerializer.");

        return message.ToString();
    }

    private static string describe(Type sagaType, CosmosSerializer serializer)
    {
        var member = SagaChain.DetermineSagaIdMember(sagaType, sagaType);
        return
            $"{sagaType.FullNameInCode()} (identity member '{member?.Name}' is serialized as '{DetermineJsonPropertyName(sagaType, serializer)}')";
    }

    /// <summary>
    /// A saga instance with the probe written into its identity member. <paramref name="stamped"/> is false
    /// when the member would not take it, which is fine -- the document is still worth serializing to see
    /// whether it has an id.
    /// </summary>
    private static object? tryStamp(Type sagaType, MemberInfo member, out bool stamped)
    {
        stamped = false;

        object saga;
        try
        {
            saga = Activator.CreateInstance(sagaType)!;
        }
        catch (Exception)
        {
            // No usable default constructor. Wolverine's CreateNewSagaFrame already refuses that saga with a
            // better message than anything this guard could add
            return null;
        }

        try
        {
            switch (member)
            {
                case PropertyInfo { CanWrite: true } property when property.PropertyType == typeof(string):
                    property.SetValue(saga, Probe);
                    stamped = true;
                    break;

                case FieldInfo field when field.FieldType == typeof(string):
                    field.SetValue(saga, Probe);
                    stamped = true;
                    break;
            }
        }
        catch (Exception)
        {
            stamped = false;
        }

        return saga;
    }

    private static JsonDocument? trySerialize(object saga, CosmosSerializer serializer)
    {
        try
        {
            using var stream = serializer.ToStream(saga);
            return JsonDocument.Parse(stream);
        }
        catch (Exception)
        {
            // A custom CosmosSerializer is free to reject a type it was never meant to see. We are in no
            // position to second guess it
            return null;
        }
    }
}

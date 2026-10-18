using System.Reflection;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Bobcat.CritterStack;

/// <summary>
/// The document half of the store, reached the same store-agnostic way <see cref="EventStores"/>
/// reaches the event half: through the <c>JasperFx.Events.Documents</c> abstractions, with no
/// reference to Marten, Polecat or Fisher (issue #270).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The shipped Critter Stack vocabulary assumed event sourcing, so an
/// ordinary document-backed Wolverine application — <c>Storage.Insert</c>, <c>[Entity]</c>, a
/// revisioned document — could arrange nothing, assert nothing about its own state, and had to
/// write a private grammar before it could write its first scenario. Four of the ten shipped steps
/// applied to it, and the four were the messaging and HTTP halves.
/// </para>
/// <para>
/// <b>How the store is found, and why nothing new has to be registered.</b> On Marten and Fisher
/// alike the concrete store object implements <see cref="IEventStore"/> <em>and</em>
/// <see cref="IDocumentSessionFactory"/> — the same instance, one registration. So the document
/// steps resolve exactly what the event steps already resolve, through the host's
/// <c>IHostResource</c>, and cast. A store that is one without the other is named in the
/// exception rather than silently producing an empty result.
/// </para>
/// <para>
/// <b>Why the load is reflected, and why it is public.</b> <c>LoadAsync&lt;T&gt;</c> is
/// generic-only on every store, while a <c>{document}</c> capture yields nothing but a
/// <see cref="Type"/> — so something has to close the generic at run time. Every grammar taking a
/// type-name capture meets the same wall, which is why it is solved once here and exposed, rather
/// than re-derived in each consumer's copy. Same reasoning as making
/// <see cref="RecordBuilding"/> public (issue #272).
/// </para>
/// </remarks>
public static class DocumentStores
{
    /// <summary>
    /// The document-session factory behind an event store handle. Both are the same object on
    /// every Critter Stack store, so this is a checked cast with a message rather than a lookup.
    /// </summary>
    public static IDocumentSessionFactory SessionFactoryFor(IEventStore store)
        => store as IDocumentSessionFactory
           ?? throw new InvalidOperationException(
               $"The store {EventStores.Describe(store)} is a JasperFx.Events.IEventStore but not a "
               + "JasperFx.Events.Documents.IDocumentSessionFactory, so Bobcat cannot reach its documents. "
               + "Marten, Polecat and Fisher all implement both on the one store object.");

    /// <summary>
    /// Load one document of <paramref name="documentType"/> by id, or null when there is none.
    /// The reflected close over <c>LoadAsync&lt;T&gt;</c> that a <c>{document}</c>-captured step
    /// cannot avoid.
    /// </summary>
    public static async Task<object?> LoadAsync(IEventStore store, Type documentType, object id,
        CancellationToken token = default)
    {
        var session = SessionFactoryFor(store).QuerySession();
        try
        {
            var load = loadMethod(documentType);
            var task = (Task)load.Invoke(session, [id, token])!;
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }
        finally
        {
            await disposeAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Store every document — all of <paramref name="documentType"/> — and commit, which is the
    /// arrange half of a document-backed spec: the rows the behaviour under test runs against.
    /// </summary>
    public static Task StoreAllAsync(IEventStore store, Type documentType,
        IReadOnlyList<object> documents, CancellationToken token = default)
        => StoreAllAsync(store, documentType, documents, null, token);

    /// <summary>
    /// bobcat#536. <see cref="StoreAllAsync(IEventStore, Type, IReadOnlyList{object}, CancellationToken)"/> for one
    /// tenant: stored and committed through a session opened for <paramref name="tenantId"/> on the database that
    /// tenant lives in. A null tenant is the store's lightweight session.
    /// </summary>
    public static async Task StoreAllAsync(IEventStore store, Type documentType,
        IReadOnlyList<object> documents, string? tenantId, CancellationToken token = default)
    {
        if (documents.Count == 0) return;

        var session = tenantId is null ? SessionFactoryFor(store).LightweightSession() : await TenantSessionAsync(store, tenantId, token);
        try
        {
            if (session is not IDocumentWriteOperations writes)
                throw new InvalidOperationException(
                    $"The lightweight session {session.GetType().FullName} opened by "
                    + $"{EventStores.Describe(store)} is not a JasperFx.Events.Documents.IDocumentWriteOperations, "
                    + "so Bobcat cannot store documents through it.");

            // Store<T>(params T[]) — closed over the document type so the store writes to the
            // right table. A typed array, not object[]: the params element type is T.
            var typed = Array.CreateInstance(documentType, documents.Count);
            for (var i = 0; i < documents.Count; i++) typed.SetValue(documents[i], i);

            storeMethod(documentType).Invoke(writes, [typed]);

            await session.SaveChangesAsync(token).ConfigureAwait(false);
        }
        finally
        {
            await disposeAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// bobcat#536. A document session for one tenant, opened on the database that tenant lives in. Not
    /// <c>IDocumentSessionFactory.LightweightSession(tenantId)</c>: Marten does not implement that overload, so it
    /// throws there. The event store's own tenant session (<see cref="EventStores.OpenTenantSessionAsync"/>) is the
    /// same session on every store, and is one.
    /// </summary>
    public static async Task<IDocumentSessionOperations> TenantSessionAsync(IEventStore store, string tenantId,
        CancellationToken token = default)
    {
        var session = await EventStores.OpenTenantSessionAsync(store, tenantId, token).ConfigureAwait(false);
        if (session is IDocumentSessionOperations documents) return documents;

        await session.DisposeAsync().ConfigureAwait(false);
        throw new InvalidOperationException(
            $"The tenant session {session.GetType().FullName} opened by {EventStores.Describe(store)} is not a "
            + "JasperFx.Events.Documents.IDocumentSessionOperations, so Bobcat cannot reach that tenant's documents.");
    }

    /// <summary>
    /// The id a step wrote as text, as the type the document is actually keyed by. Shared with the
    /// read-model steps' rule, and for the same reason: <c>LoadAsync&lt;T&gt;</c> is chosen by the
    /// id's CLR type, so handing a Guid-keyed document a string finds nothing at all — a false
    /// "no such document" rather than an error.
    /// </summary>
    public static object IdentityOf(Type documentType, string id)
    {
        var identity = documentType.GetProperty("Id")?.PropertyType
                       ?? documentType.GetField("Id")?.FieldType;

        if (identity is null) return Guid.TryParse(id, out var guid) ? guid : id;

        try
        {
            return Runtime.CellValues.Read(id, identity)
                   ?? throw new Engine.SpecCriticalException(
                       $"'{id}' is not a usable {documentType.Name} identity — it converted to null.");
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            throw new Engine.SpecCriticalException(
                $"'{id}' is not a valid {identity.Name}, which is what {documentType.Name}.Id is keyed by.", e);
        }
    }

    private static MethodInfo loadMethod(Type documentType)
        => typeof(IDocumentReadOperations)
               .GetMethods(BindingFlags.Public | BindingFlags.Instance)
               .Single(m => m.Name == nameof(IDocumentReadOperations.LoadAsync)
                            && m.IsGenericMethodDefinition
                            && m.GetParameters() is [{ ParameterType.IsGenericParameter: false } first, _]
                            && first.ParameterType == typeof(object))
               .MakeGenericMethod(documentType);

    private static MethodInfo storeMethod(Type documentType)
        => typeof(IDocumentWriteOperations)
               .GetMethods(BindingFlags.Public | BindingFlags.Instance)
               .Single(m => m.Name == nameof(IDocumentWriteOperations.Store) && m.IsGenericMethodDefinition)
               .MakeGenericMethod(documentType);

    private static ValueTask disposeAsync(object session)
        => session switch
        {
            IAsyncDisposable async => async.DisposeAsync(),
            IDisposable sync => Dispose(sync),
            _ => ValueTask.CompletedTask,
        };

    private static ValueTask Dispose(IDisposable disposable)
    {
        disposable.Dispose();
        return ValueTask.CompletedTask;
    }
}

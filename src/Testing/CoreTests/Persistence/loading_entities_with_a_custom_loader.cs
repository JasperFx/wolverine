using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
/// [Entity(Loader = typeof(...))] loads from something Wolverine has no persistence provider for.
/// The tests use an object-store-shaped fake — keyed by tenant and id, answering null for a key it
/// does not hold — because that is the case the feature exists for. No database is involved, which
/// is the point: a loader-backed entity needs no configured persistence at all.
/// </summary>
public class loading_entities_with_a_custom_loader : IAsyncLifetime
{
    private IHost _host = null!;
    private FakeObjectStore _store = null!;
    private Recorder _recorder = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(DocumentHandlers))
                    .IncludeType(typeof(RegisteredLoaderHandler))
                    .IncludeType(typeof(InterfaceLoaderHandler))
                    .IncludeType(typeof(BeforeMethodLoaderHandler));

                opts.Services.AddSingleton<FakeObjectStore>();
                opts.Services.AddSingleton<Recorder>();
                opts.Services.AddSingleton<IDocumentSource, DocumentSource>();

                // The registry form: every [Entity] of this type is loaded by this loader, so the
                // handlers reading it need nothing more than a plain attribute.
                opts.EntityDefaults.LoadWith<RegisteredDocument, RegisteredDocumentLoader>();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<FakeObjectStore>();
        _recorder = _host.Services.GetRequiredService<Recorder>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task loads_the_entity_and_passes_it_to_the_handler()
    {
        _store.Save("acme", "one", new Document("one", "Hello"));

        await _host.MessageBus().InvokeForTenantAsync("acme", new ReadDocument("one"), TestContext.Current.CancellationToken);

        _recorder.Read.ShouldBe(["Hello"]);
    }

    [Fact]
    public async Task the_loader_sees_the_tenant_so_the_same_id_resolves_per_tenant()
    {
        _store.Save("acme", "one", new Document("one", "Acme copy"));
        _store.Save("globex", "one", new Document("one", "Globex copy"));

        await _host.MessageBus().InvokeForTenantAsync("globex", new ReadDocument("one"), TestContext.Current.CancellationToken);

        _recorder.Read.ShouldBe(["Globex copy"]);
    }

    [Fact]
    public async Task a_required_entity_that_is_missing_stops_the_handler()
    {
        // Not an exception: the default is to log that the data was missing and stop cleanly.
        await _host.MessageBus().InvokeForTenantAsync("acme", new ReadDocument("nothing-here"), TestContext.Current.CancellationToken);

        _recorder.Read.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_optional_entity_that_is_missing_arrives_as_null()
    {
        await _host.MessageBus().InvokeForTenantAsync("acme", new MaybeReadDocument("nothing-here"), TestContext.Current.CancellationToken);

        _recorder.Read.ShouldBe(["(missing)"]);
    }

    [Fact]
    public async Task throws_when_asked_to_and_names_the_entity_type()
    {
        var ex = await Should.ThrowAsync<RequiredDataMissingException>(() =>
            _host.MessageBus().InvokeForTenantAsync("acme", new ReadDocumentOrThrow("nothing-here")));

        // No identity variable exists on a loader-backed entity, so the stock message cannot name
        // one and says what it does know instead.
        ex.Message.ShouldBe("Required Document was not found");
    }

    [Fact]
    public async Task uses_a_supplied_missing_message_verbatim()
    {
        var ex = await Should.ThrowAsync<RequiredDataMissingException>(() =>
            _host.MessageBus().InvokeForTenantAsync("acme", new ReadDocumentOrComplain("nothing-here")));

        ex.Message.ShouldBe("That document is not in the bucket");
    }

    [Fact]
    public async Task a_missing_message_needing_escapes_still_generates_compiling_code()
    {
        // The message is baked into generated source as a string literal, so a quote or a backslash
        // in it has to be escaped on the way in — otherwise the handler assembly does not compile at
        // all and this whole fixture fails to start.
        var ex = await Should.ThrowAsync<RequiredDataMissingException>(() =>
            _host.MessageBus().InvokeForTenantAsync("acme", new ReadDocumentOrQuote("nothing-here")));

        ex.Message.ShouldBe("No \"Document\" under C:\\bucket\\path");
    }

    [Fact]
    public async Task a_static_loader_class_needs_no_instance()
    {
        await _host.MessageBus().InvokeAsync(new ReadConstant(), TestContext.Current.CancellationToken);

        _recorder.Read.ShouldBe(["always here"]);
    }

    [Fact]
    public async Task the_loader_can_be_an_interface_resolved_from_the_container()
    {
        _store.Save("acme", "via-interface", new Document("via-interface", "Through the interface"));

        await _host.MessageBus().InvokeForTenantAsync("acme", new ReadThroughInterface("via-interface"),
            TestContext.Current.CancellationToken);

        _recorder.Read.ShouldBe(["Through the interface"]);
    }

    [Fact]
    public async Task the_loaded_entity_can_arrive_on_a_before_method_and_relay_to_handle()
    {
        _store.Save("acme", "on-load", new Document("on-load", "Seen by LoadAsync"));

        await _host.MessageBus().InvokeForTenantAsync("acme", new ReadOnBeforeMethod("on-load"),
            TestContext.Current.CancellationToken);

        _recorder.Read.ShouldBe(["Seen by LoadAsync/Seen by LoadAsync"]);
    }

    [Fact]
    public async Task a_missing_entity_on_a_before_method_still_stops_the_handler()
    {
        await _host.MessageBus().InvokeForTenantAsync("acme", new ReadOnBeforeMethod("nothing-here"),
            TestContext.Current.CancellationToken);

        _recorder.Read.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_registered_loader_is_used_by_a_plain_entity_attribute()
    {
        _store.Save("acme", "registered", new RegisteredDocument("registered", "From the registry"));

        await _host.MessageBus().InvokeForTenantAsync("acme", new ReadRegisteredDocument("registered"), TestContext.Current.CancellationToken);

        _recorder.Read.ShouldBe(["From the registry"]);
    }
}

public class rejecting_invalid_entity_loaders
{
    [Fact]
    public void no_load_method_returning_the_entity_type()
    {
        var ex = Should.Throw<InvalidEntityLoaderException>(() =>
            new EntityDefaults().LoadWith<Document, LoaderWithoutALoadMethod>());

        ex.Message.ShouldContain("found none");
    }

    [Fact]
    public void more_than_one_candidate_load_method()
    {
        var ex = Should.Throw<InvalidEntityLoaderException>(() =>
            new EntityDefaults().LoadWith<Document, LoaderWithTwoLoadMethods>());

        ex.Message.ShouldContain("Found 2 candidate");
    }
}

#region Test types

public record Document(string Id, string Body);

public record RegisteredDocument(string Id, string Body);

public record ReadDocument(string Id);

public record MaybeReadDocument(string Id);

public record ReadDocumentOrThrow(string Id);

public record ReadDocumentOrComplain(string Id);

public record ReadDocumentOrQuote(string Id);

public record ReadConstant;

public record ReadRegisteredDocument(string Id);

public record ReadThroughInterface(string Id);

public record ReadOnBeforeMethod(string Id);

/// <summary>What the handlers actually saw, so the assertions do not depend on message routing.</summary>
public class Recorder
{
    public List<string> Read { get; } = [];
}

/// <summary>
/// Stands in for an object store: keyed by tenant and id, and a key it does not hold is null rather
/// than an error.
/// </summary>
public class FakeObjectStore
{
    private readonly Dictionary<(string Tenant, string Id), object> _objects = new();

    public void Save<T>(string tenant, string id, T document) where T : notnull
        => _objects[(tenant, id)] = document;

    public Task<T?> LoadAsync<T>(string tenant, string id, CancellationToken _) where T : class
        => Task.FromResult(_objects.TryGetValue((tenant, id), out var found) ? found as T : null);
}

/// <summary>
/// An instance loader with a dependency of its own. Its parameters are resolved out of the chain the
/// same way a handler's are, which is what lets it address the object by tenant *and* id.
/// </summary>
public class DocumentLoader(FakeObjectStore store)
{
    public Task<Document?> LoadAsync(TenantId tenant, string id, CancellationToken cancellationToken)
        => store.LoadAsync<Document>(tenant.Value, id, cancellationToken);
}

public class RegisteredDocumentLoader(FakeObjectStore store)
{
    public Task<RegisteredDocument?> LoadAsync(TenantId tenant, string id, CancellationToken cancellationToken)
        => store.LoadAsync<RegisteredDocument>(tenant.Value, id, cancellationToken);
}

/// <summary>
/// A loader named by its interface. Wolverine resolves the registered implementation from the
/// container and calls it, so an existing store abstraction can be pointed at directly.
/// </summary>
public interface IDocumentSource
{
    Task<Document?> LoadAsync(TenantId tenant, string id, CancellationToken cancellationToken);
}

public class DocumentSource(FakeObjectStore store) : IDocumentSource
{
    public Task<Document?> LoadAsync(TenantId tenant, string id, CancellationToken cancellationToken)
        => store.LoadAsync<Document>(tenant.Value, id, cancellationToken);
}

public static class ConstantDocumentLoader
{
    public static Document Load() => new("constant", "always here");
}

public class LoaderWithoutALoadMethod
{
    public Task<Document?> FetchAsync(string id) => Task.FromResult<Document?>(null);
}

public class LoaderWithTwoLoadMethods
{
    public Document? Load(string id) => null;
    public Task<Document?> LoadAsync(string id) => Task.FromResult<Document?>(null);
}

public static class DocumentHandlers
{
    public static void Handle(
        ReadDocument _,
        [Entity(Loader = typeof(DocumentLoader))] Document document,
        Recorder recorder)
        => recorder.Read.Add(document.Body);

    public static void Handle(
        MaybeReadDocument _,
        [Entity(Loader = typeof(DocumentLoader), Required = false)] Document? document,
        Recorder recorder)
        => recorder.Read.Add(document?.Body ?? "(missing)");

    public static void Handle(
        ReadDocumentOrThrow _,
        [Entity(Loader = typeof(DocumentLoader), OnMissing = OnMissing.ThrowException)] Document document,
        Recorder recorder)
        => recorder.Read.Add(document.Body);

    public static void Handle(
        ReadDocumentOrComplain _,
        [Entity(Loader = typeof(DocumentLoader), OnMissing = OnMissing.ThrowException,
            MissingMessage = "That document is not in the bucket")]
        Document document,
        Recorder recorder)
        => recorder.Read.Add(document.Body);

    public static void Handle(
        ReadDocumentOrQuote _,
        [Entity(Loader = typeof(DocumentLoader), OnMissing = OnMissing.ThrowException,
            MissingMessage = "No \"Document\" under C:\\bucket\\path")]
        Document document,
        Recorder recorder)
        => recorder.Read.Add(document.Body);

    public static void Handle(
        ReadConstant _,
        [Entity(Loader = typeof(ConstantDocumentLoader))] Document document,
        Recorder recorder)
        => recorder.Read.Add(document.Body);
}

public static class InterfaceLoaderHandler
{
    public static void Handle(
        ReadThroughInterface _,
        [Entity(Loader = typeof(IDocumentSource))] Document document,
        Recorder recorder)
        => recorder.Read.Add(document.Body);
}

/// <summary>
/// The shape a real handler tends to have: the entity is a parameter of the LoadAsync "before"
/// method, which derives something from it, and both that and the entity itself reach Handle.
/// </summary>
public static class BeforeMethodLoaderHandler
{
    public static string LoadAsync(
        ReadOnBeforeMethod _,
        [Entity(Loader = typeof(DocumentLoader))] Document document)
        => document.Body;

    public static void Handle(
        ReadOnBeforeMethod _,
        Document document,
        string derived,
        Recorder recorder)
        => recorder.Read.Add($"{derived}/{document.Body}");
}

public static class RegisteredLoaderHandler
{
    public static void Handle(
        ReadRegisteredDocument _,
        [Entity] RegisteredDocument document,
        Recorder recorder)
        => recorder.Read.Add(document.Body);
}

#endregion

using Alba;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence;

namespace Wolverine.Http.Tests.Persistence;

/// <summary>
/// [Entity(Loader = typeof(...))] in an HTTP endpoint. The point of these tests is that the endpoint
/// keeps every bit of the missing-data handling a database-backed [Entity] gets — a 404, or a 404
/// with a ProblemDetails body — while the entity itself comes out of an object store that no
/// persistence provider knows about. Nothing here reaches a database; see the note on the Marten
/// registration below, which exists only so the rest of the assembly's endpoints can be discovered.
/// </summary>
public class custom_entity_loader_http : IAsyncLifetime
{
    private IAlbaHost _host = null!;
    private ObjectStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddSingleton<ObjectStore>();

        // HTTP endpoint discovery scans whole assemblies and its Includes are additive, so the other
        // endpoints in this test assembly come along and some of them need an IDocumentStore
        // registration to have their parameters matched. Marten is registered for their benefit and
        // pointed at a port nothing listens on: the endpoints under test never touch it, and without
        // IntegrateWithWolverine() nothing connects on startup either. This test needs no database.
        builder.Services.AddMarten(opts => opts.Connection(
            "Host=localhost;Port=9999;Database=does_not_exist;Username=nobody;Password=nobody;Timeout=2;Command Timeout=2"));

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery
                .DisableConventionalDiscovery()
                .IncludeType(typeof(ObjectStoreEndpoints));

            opts.ApplicationAssembly = GetType().Assembly;
        });

        builder.Services.AddWolverineHttp();

        _host = await AlbaHost.For(builder, app => { app.MapWolverineEndpoints(); });
        _store = _host.Services.GetRequiredService<ObjectStore>();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task loads_the_object_using_the_route_argument()
    {
        _store.Save("invoice-1", new StoredDocument("invoice-1", "12 claim lines"));

        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/object-store/document/invoice-1");
            x.StatusCodeShouldBeOk();
        });

        var document = await result.ReadAsJsonAsync<StoredDocument>();
        document!.Contents.ShouldBe("12 claim lines");
    }

    [Fact]
    public async Task a_missing_object_is_a_404()
    {
        await _host.Scenario(x =>
        {
            x.Get.Url("/object-store/document/not-in-the-bucket");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task a_missing_object_can_answer_problem_details_instead()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/object-store/document-with-problems/not-in-the-bucket");
            x.StatusCodeShouldBe(404);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var details = await result.ReadAsJsonAsync<ProblemDetails>();
        details!.Detail.ShouldBe("That document is not in the bucket yet");
    }
}

/// <summary>Stands in for an object store — a key it does not hold answers null, not an error.</summary>
public class ObjectStore
{
    private readonly Dictionary<string, StoredDocument> _objects = new();

    public void Save(string key, StoredDocument document) => _objects[key] = document;

    public Task<StoredDocument?> LoadAsync(string key, CancellationToken _)
        => Task.FromResult(_objects.GetValueOrDefault(key));
}

public record StoredDocument(string Key, string Contents);

/// <summary>
/// The loader. Its <c>key</c> parameter is bound to the endpoint's <c>{key}</c> route argument by
/// name, and the store arrives from DI the same way it would in a handler.
/// </summary>
public class StoredDocumentLoader(ObjectStore store)
{
    public Task<StoredDocument?> LoadAsync(string key, CancellationToken cancellationToken)
        => store.LoadAsync(key, cancellationToken);
}

public static class ObjectStoreEndpoints
{
    [WolverineGet("/object-store/document/{key}")]
    public static StoredDocument Get([Entity(Loader = typeof(StoredDocumentLoader))] StoredDocument document)
        => document;

    [WolverineGet("/object-store/document-with-problems/{key}")]
    public static StoredDocument GetWithProblems(
        [Entity(Loader = typeof(StoredDocumentLoader), OnMissing = OnMissing.ProblemDetailsWith404,
            MissingMessage = "That document is not in the bucket yet")]
        StoredDocument document)
        => document;
}

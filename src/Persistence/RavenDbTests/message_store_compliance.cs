using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Embedded;
using Raven.TestDriver;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.Transports.Tcp;

namespace RavenDbTests;

public class DatabaseFixture : RavenTestDriver
{
    private static bool _configured;

    public IDocumentStore StartRavenStore()
    {
        EnsureServerConfigured();
        return GetDocumentStore();
    }

    internal static void EnsureServerConfigured()
    {
        if (_configured) return;
        _configured = true;

        // Configure the embedded RavenDB server.
        // RavenDB.TestDriver 7.0.x requires .NET 8.0.15+ runtime.
        // We try to use a brew-installed .NET 8 if available, otherwise fall back to system dotnet.
        var options = new TestServerOptions
        {
            FrameworkVersion = null, // Use available runtime
            Licensing = new ServerOptions.LicensingOptions
            {
                ThrowOnInvalidOrMissingLicense = false // Don't require license for tests
            }
        };

        // Check for brew-installed .NET 8 which has newer runtime
        var brewDotNetPath = "/opt/homebrew/opt/dotnet@8/bin/dotnet";
        if (File.Exists(brewDotNetPath))
        {
            options.DotNetPath = brewDotNetPath;
        }

        ConfigureServer(options);
    }
}

[CollectionDefinition("raven")]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

[Collection("raven")]
public class message_store_compliance : MessageStoreCompliance
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store;

    public message_store_compliance(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public override async Task<IHost> BuildCleanHost()
    {
        var store = _fixture.StartRavenStore();
        _store = store;

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // TODO -- TEMP!
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseRavenDbPersistence();
                opts.Services.AddSingleton<IDocumentStore>(store);

                opts.ListenAtPort(2345).UseDurableInbox();
            }).StartAsync();

        return host;
    }

    [Fact]
    public async Task marks_envelope_as_having_an_expires_on_mark_handled()
    {
        var envelope = ObjectMother.Envelope();

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        using var session = _store.OpenAsyncSession();
        var incoming = await session.LoadAsync<IncomingMessage>(envelope.Id.ToString());
        var metadata = session.Advanced.GetMetadataFor(incoming);
        metadata.TryGetValue("@expires", out var raw).ShouldBeTrue();

        var value = metadata["@expires"];
        Debug.WriteLine(value);

    }


}

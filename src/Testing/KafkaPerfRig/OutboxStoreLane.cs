using System.Diagnostics;
using System.Text.Json;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RavenDb;
using Wolverine.SqlServer;

namespace KafkaPerfRig;

/// <summary>
/// GH-4369. A direct A/B of <c>IMessageOutbox.StoreOutgoingAsync</c>, one store at a time.
///
/// <para>
/// Deliberately NOT an end-to-end cell. GH-4319's <c>local-queue</c> lane measures a whole publish
/// path because that is what it changed; GH-4369 changes exactly one method on three stores, so the
/// honest instrument is that method against the real database. An end-to-end cell would bury a
/// per-store round-trip change under broker time and tell you less.
/// </para>
///
/// <para>
/// Each round stores a fresh batch of <c>RIG_STORE_BATCH</c> envelopes twice — once as N calls to the
/// single-envelope overload (the pre-GH-4369 shape for these stores, and still the default-interface
/// behaviour for any store that does not override the batch), once as one call to the batch overload.
/// The two arms alternate order round to round so neither is systematically advantaged by cache
/// warmth. Envelopes are distinct objects per arm, so neither arm is ever writing rows the other
/// already wrote.
/// </para>
///
/// <para>
/// <c>RIG_STORE</c>: postgresql (default) | sqlserver | oracle | ravendb. Cosmos DB is absent on
/// purpose — the only Cosmos available here is the emulator, and GH-4331 is the standing lesson about
/// quoting numbers an emulator produced.
/// </para>
/// </summary>
public static class OutboxStoreLane
{
    public static async Task RunAsync(RigConfig cfg)
    {
        var store = Environment.GetEnvironmentVariable("RIG_STORE") ?? "postgresql";
        var batchSize = cfg.StoreIncomingBatchSize > 0 ? cfg.StoreIncomingBatchSize : 100;
        var rounds = envInt("RIG_ROUNDS", 20);
        var warmup = envInt("RIG_WARMUP_ROUNDS", 5);

        using var host = await buildHostAsync(store, cfg);
        var messageStore = host.Services.GetRequiredService<IMessageStore>();
        var outbox = messageStore.Outbox;
        var admin = messageStore.Admin;

        await admin.ClearAllAsync();

        Console.WriteLine(
            $"[rig] outbox-store: store={store} batch={batchSize} rounds={rounds} (+{warmup} warmup)");

        var sequential = new List<double>();
        var batched = new List<double>();

        for (var round = 0; round < rounds + warmup; round++)
        {
            var measured = round >= warmup;

            // Alternate which arm goes first so neither one systematically owns the cold cache
            if (round % 2 == 0)
            {
                var a = await timeSequentialAsync(outbox, batchSize);
                var b = await timeBatchedAsync(outbox, batchSize);
                if (measured) { sequential.Add(a); batched.Add(b); }
            }
            else
            {
                var b = await timeBatchedAsync(outbox, batchSize);
                var a = await timeSequentialAsync(outbox, batchSize);
                if (measured) { sequential.Add(a); batched.Add(b); }
            }

            // Keep the table from growing across rounds; a table that gets steadily larger is a
            // confound that would flatter whichever arm runs first
            await admin.ClearAllAsync();
        }

        var summary = new Dictionary<string, object>
        {
            ["store"] = store,
            ["batchSize"] = batchSize,
            ["rounds"] = rounds,
            ["sequential_ms"] = describe(sequential),
            ["batched_ms"] = describe(batched),
            ["speedup_at_p50"] = Math.Round(percentile(sequential, 50) / Math.Max(percentile(batched, 50), 0.0001), 2)
        };

        Directory.CreateDirectory(cfg.OutDir);
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(cfg.OutDir, $"outbox-store-{store}.json"), json);
        Console.WriteLine(json);

        await host.StopAsync();
    }

    private static async Task<double> timeSequentialAsync(IMessageOutbox outbox, int count)
    {
        var envelopes = buildEnvelopes(count);
        var start = Stopwatch.GetTimestamp();
        foreach (var envelope in envelopes)
        {
            await outbox.StoreOutgoingAsync(envelope, 1);
        }

        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static async Task<double> timeBatchedAsync(IMessageOutbox outbox, int count)
    {
        var envelopes = buildEnvelopes(count);
        var start = Stopwatch.GetTimestamp();
        await outbox.StoreOutgoingAsync(envelopes, 1);
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static Envelope[] buildEnvelopes(int count)
    {
        var destination = new Uri("rig://outbox");
        var envelopes = new Envelope[count];
        for (var i = 0; i < count; i++)
        {
            envelopes[i] = new Envelope
            {
                Id = Guid.NewGuid(),
                Destination = destination,
                MessageType = "rig.outbox.message",
                ContentType = "application/json",
                Data = System.Text.Encoding.UTF8.GetBytes(Payloads.Small),
                Status = EnvelopeStatus.Outgoing,
                OwnerId = 1
            };
        }

        return envelopes;
    }

    private static Task<IHost> buildHostAsync(string store, RigConfig cfg)
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ApplicationAssembly = typeof(LocalRigHandlers).Assembly;

                switch (store)
                {
                    case "sqlserver":
                        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, cfg.PostgresSchema);
                        break;

                    case "oracle":
                        opts.PersistMessagesWithOracle(Servers.OracleConnectionString);
                        break;

                    case "ravendb":
                        opts.Services.AddSingleton<IDocumentStore>(_ =>
                        {
                            var documentStore = new DocumentStore
                            {
                                Urls = [Environment.GetEnvironmentVariable("RIG_RAVEN_URL") ?? "http://localhost:8080"],
                                Database = "rig_outbox"
                            };
                            documentStore.Initialize();

                            // RavenDB will not create the database on first use the way the SQL stores
                            // create their schema, and Wolverine's own startup queries it immediately
                            try
                            {
                                documentStore.Maintenance.Server.Send(
                                    new CreateDatabaseOperation(new DatabaseRecord("rig_outbox")));
                            }
                            catch (ConcurrencyException)
                            {
                                // Already there
                            }

                            return documentStore;
                        });
                        opts.UseRavenDbPersistence();
                        break;

                    default:
                        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, cfg.PostgresSchema);
                        break;
                }
            });

        return builder.StartAsync();
    }

    private static Dictionary<string, double> describe(List<double> values)
    {
        return new Dictionary<string, double>
        {
            ["p50"] = percentile(values, 50),
            ["p95"] = percentile(values, 95),
            ["min"] = values.Count == 0 ? 0 : Math.Round(values.Min(), 3),
            ["max"] = values.Count == 0 ? 0 : Math.Round(values.Max(), 3),
            ["mean"] = values.Count == 0 ? 0 : Math.Round(values.Average(), 3)
        };
    }

    private static double percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(x => x).ToArray();
        var index = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return Math.Round(sorted[Math.Clamp(index, 0, sorted.Length - 1)], 3);
    }

    private static int envInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) ? value : fallback;
    }
}

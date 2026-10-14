using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Xunit;

namespace PostgresqlTests.Transport;

public class Bug5015DurablePopResponseLoss : PostgresqlContext
{
    private enum ResponseDropPoint
    {
        PopResult,
        Commit
    }

    private readonly record struct BackendMessage(byte Type, string? CommandTag, int EndOffset, int MessageLength);

    [Fact]
    public async Task LostPopResponseDoesNotStrandMessagesOnTheLiveOwner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var schema = $"bug_5015_{Guid.NewGuid():N}";
        IHost? host = null;

        try
        {
            host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, schema,
                        transportSchema: schema);
                    opts.ListenToPostgresqlQueue("pop_timeout").PollingInterval(1.Hours());
                    opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                    opts.Durability.ScheduledJobPollingTime = 1.Hours();
                }).StartAsync(cancellationToken);

            var runtime = host.GetRuntime();
            var queue = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["pop_timeout"];
            await runtime.Endpoints.StopListenerAsync(queue, cancellationToken);
            queue.MaximumMessagesToReceive = 1;

            var envelope = ObjectMother.Envelope();
            await queue.SendAsync(envelope);
            (await queue.CountAsync()).ShouldBe(1);

            var postgresConnection = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);
            await using var proxy = await PostgresqlReplyDropProxy.StartAsync(
                postgresConnection.Host ?? "localhost", postgresConnection.Port, ResponseDropPoint.PopResult);

            var proxiedConnectionString = new NpgsqlConnectionStringBuilder(postgresConnection.ConnectionString)
            {
                Host = "127.0.0.1",
                Port = proxy.Port,
                SslMode = SslMode.Disable,
                Timeout = 5,
                CommandTimeout = 2
            };

            await using var proxiedDataSource = NpgsqlDataSource.Create(proxiedConnectionString.ConnectionString);
            var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());

            await using (var faultedListener = new PostgresqlQueueListener(queue, runtime, receiver,
                             proxiedDataSource, null))
            {
                var failure = await Record.ExceptionAsync(() => faultedListener.TryPopDurablyAsync(1,
                    runtime.DurabilitySettings, NullLogger.Instance, cancellationToken));

                failure.ShouldNotBeNull("the proxy intentionally drops the completed pop response");
                await proxy.ResponseDropped.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            // With an implicit transaction the pop is already committed at this point: the queue is empty
            // and the inbox row is owned by this live node, but the listener never received its envelope.
            // The fixed implementation keeps the transaction open through result materialization, so a
            // dropped response rolls the pop back and leaves the message available for another poll.
            (await queue.CountAsync()).ShouldBe(1);

            await using var retryListener = new PostgresqlQueueListener(queue, runtime, receiver, queue.DataSource,
                null);
            var recovered = await retryListener.TryPopDurablyAsync(1, runtime.DurabilitySettings,
                NullLogger.Instance, cancellationToken);

            recovered.Select(x => x.Id).ShouldBe(new[] { envelope.Id });
        }
        finally
        {
            if (host != null)
            {
                await host.StopAsync(cancellationToken);
                host.Dispose();
            }

            await using var connection = new NpgsqlConnection(Servers.PostgresConnectionString);
            await connection.OpenAsync(cancellationToken);
            await connection.DropSchemaAsync(schema, ct: cancellationToken);
        }
    }

    [Fact]
    public async Task LostCommitReplyReleasesCommittedRowsForRecovery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var schema = $"bug_5015_{Guid.NewGuid():N}";
        IHost? host = null;

        try
        {
            host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, schema,
                        transportSchema: schema);
                    opts.ListenToPostgresqlQueue("pop_timeout").PollingInterval(1.Hours());
                    opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                    opts.Durability.ScheduledJobPollingTime = 1.Hours();
                }).StartAsync(cancellationToken);

            var runtime = host.GetRuntime();
            var queue = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["pop_timeout"];
            await runtime.Endpoints.StopListenerAsync(queue, cancellationToken);
            runtime.DurabilitySettings.AssignedNodeNumber.ShouldNotBe(0);
            queue.MaximumMessagesToReceive = 1;

            var envelope = ObjectMother.Envelope();
            await queue.SendAsync(envelope);
            (await queue.CountAsync()).ShouldBe(1);

            var postgresConnection = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);
            await using var proxy = await PostgresqlReplyDropProxy.StartAsync(
                postgresConnection.Host ?? "localhost", postgresConnection.Port, ResponseDropPoint.Commit);

            var proxiedConnectionString = new NpgsqlConnectionStringBuilder(postgresConnection.ConnectionString)
            {
                Host = "127.0.0.1",
                Port = proxy.Port,
                SslMode = SslMode.Disable,
                Timeout = 5,
                CommandTimeout = 2
            };

            await using var proxiedDataSource = NpgsqlDataSource.Create(proxiedConnectionString.ConnectionString);
            var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());

            await using (var faultedListener = new PostgresqlQueueListener(queue, runtime, receiver,
                             proxiedDataSource, null))
            {
                var failure = await Record.ExceptionAsync(() => faultedListener.TryPopDurablyAsync(1,
                    runtime.DurabilitySettings, NullLogger.Instance, cancellationToken));

                failure.ShouldNotBeNull("the proxy intentionally drops the COMMIT response");
                await proxy.ResponseDropped.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            (await queue.CountAsync()).ShouldBe(0);

            await using var connection = new NpgsqlConnection(Servers.PostgresConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand(
                $"select owner_id from {schema}.{DatabaseConstants.IncomingTable} where id = @id");
            command.Parameters.AddWithValue("id", envelope.Id);
            var owner = await command.ExecuteScalarAsync(cancellationToken);

            ((int)owner!).ShouldBe(0);
        }
        finally
        {
            if (host != null)
            {
                await host.StopAsync(cancellationToken);
                host.Dispose();
            }

            await using var connection = new NpgsqlConnection(Servers.PostgresConnectionString);
            await connection.OpenAsync(cancellationToken);
            await connection.DropSchemaAsync(schema, ct: cancellationToken);
        }
    }

    /// <summary>
    /// Forwards PostgreSQL traffic and can drop either the durable pop result or its COMMIT response. The pop
    /// result is recognized by its SELECT command tag, so an earlier BEGIN response cannot trigger the fault.
    /// </summary>
    private sealed class PostgresqlReplyDropProxy : IAsyncDisposable
    {
        private static readonly byte[] PopQueryMarker = Encoding.UTF8.GetBytes("WITH popped AS (");
        private readonly TcpListener _listener;
        private readonly string _upstreamHost;
        private readonly int _upstreamPort;
        private readonly ResponseDropPoint _dropPoint;
        private readonly ConcurrentBag<Task> _connectionTasks = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _responseDropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _acceptTask;
        private int _targetQuerySeen;
        private int _responseWasDropped;
        private int _popResponseWasForwarded;

        private PostgresqlReplyDropProxy(string upstreamHost, int upstreamPort, ResponseDropPoint dropPoint)
        {
            _upstreamHost = upstreamHost;
            _upstreamPort = upstreamPort;
            _dropPoint = dropPoint;
            _listener = new TcpListener(IPAddress.Loopback, 0);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task ResponseDropped => _responseDropped.Task;

        public static Task<PostgresqlReplyDropProxy> StartAsync(string upstreamHost, int upstreamPort,
            ResponseDropPoint dropPoint)
        {
            var proxy = new PostgresqlReplyDropProxy(upstreamHost, upstreamPort, dropPoint);
            proxy._listener.Start();
            proxy._acceptTask = proxy.acceptConnectionsAsync();
            return Task.FromResult(proxy);
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _listener.Stop();

            if (_acceptTask != null)
            {
                try
                {
#pragma warning disable VSTHRD003 // This task is the proxy's own accept loop, started in StartAsync.
                    await _acceptTask;
#pragma warning restore VSTHRD003
                }
                catch (OperationCanceledException)
                {
                    // Expected while stopping the accept loop.
                }
                catch (SocketException) when (_cancellation.IsCancellationRequested)
                {
                    // Stopping TcpListener interrupts AcceptTcpClientAsync on some platforms with SocketException.
                }
            }

            await Task.WhenAll(_connectionTasks);

            _cancellation.Dispose();
        }

        private async Task acceptConnectionsAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }

                _connectionTasks.Add(handleConnectionAsync(client));
            }
        }

        private async Task handleConnectionAsync(TcpClient client)
        {
            using (client)
            using (var upstream = new TcpClient())
            using (var connectionCancellation =
                   CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token))
            {
                try
                {
                    await upstream.ConnectAsync(_upstreamHost, _upstreamPort, connectionCancellation.Token);
                    var fromClient = pumpClientToServerAsync(client.GetStream(), upstream.GetStream(),
                        connectionCancellation.Token);
                    var fromServer = pumpServerToClientAsync(client, upstream.GetStream(),
                        connectionCancellation.Token);

                    await Task.WhenAny(fromClient, fromServer);
                    await connectionCancellation.CancelAsync();
                    client.Dispose();
                    upstream.Dispose();

                    try
                    {
                        await Task.WhenAll(fromClient, fromServer);
                    }
                    catch (Exception) when (connectionCancellation.IsCancellationRequested)
                    {
                        // Connection teardown interrupts both pumps.
                    }
                }
                catch (Exception) when (connectionCancellation.IsCancellationRequested)
                {
                    // Expected while stopping the proxy.
                }
            }
        }

        private async Task pumpClientToServerAsync(Stream client, Stream server, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var markerWindow = Array.Empty<byte>();

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await client.ReadAsync(buffer, cancellationToken);
                if (read == 0) return;

                if (Volatile.Read(ref _targetQuerySeen) == 0)
                {
                    var combined = new byte[markerWindow.Length + read];
                    markerWindow.CopyTo(combined, 0);
                    Array.Copy(buffer, 0, combined, markerWindow.Length, read);
                    if (combined.AsSpan().IndexOf(PopQueryMarker) >= 0)
                    {
                        Interlocked.Exchange(ref _targetQuerySeen, 1);
                    }

                    var keep = Math.Min(PopQueryMarker.Length - 1, combined.Length);
                    markerWindow = combined.AsSpan(combined.Length - keep, keep).ToArray();
                }

                await server.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        private async Task pumpServerToClientAsync(TcpClient client, Stream server, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            using var pendingResponse = new MemoryStream();
            var popCommandCompleteSeen = false;
            var commitCommandCompleteSeen = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await server.ReadAsync(buffer, cancellationToken);
                if (read == 0) return;

                if (Volatile.Read(ref _targetQuerySeen) != 0 && Volatile.Read(ref _responseWasDropped) == 0)
                {
                    await pendingResponse.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                    var response = pendingResponse.GetBuffer();
                    var responseLength = (int)pendingResponse.Length;
                    var forwardedThrough = 0;

                    foreach (var message in parseBackendMessages(response, responseLength))
                    {
                        if (message.Type == (byte)'C')
                        {
                            // Any pop that moved rows: "SELECT n" for n > 0, not only a one-message batch
                            if (message.CommandTag is { } tag && tag.StartsWith("SELECT ") &&
                                int.TryParse(tag.AsSpan(7), out var rows) && rows > 0)
                            {
                                popCommandCompleteSeen = true;
                            }
                            if (message.CommandTag == "COMMIT") commitCommandCompleteSeen = true;
                        }

                        if (message.Type == (byte)'Z' && message.MessageLength == 5)
                        {
                            var shouldDrop = _dropPoint == ResponseDropPoint.PopResult
                                ? popCommandCompleteSeen
                                : Volatile.Read(ref _popResponseWasForwarded) != 0 && commitCommandCompleteSeen;

                            if (shouldDrop && Interlocked.Exchange(ref _responseWasDropped, 1) == 0)
                            {
                                _responseDropped.TrySetResult();
                                client.Dispose();
                                return;
                            }

                            if (popCommandCompleteSeen)
                            {
                                Interlocked.Exchange(ref _popResponseWasForwarded, 1);
                            }

                            await client.GetStream().WriteAsync(
                                response.AsMemory(forwardedThrough, message.EndOffset - forwardedThrough),
                                cancellationToken);
                            forwardedThrough = message.EndOffset;
                            popCommandCompleteSeen = false;
                            commitCommandCompleteSeen = false;
                        }
                    }

                    if (forwardedThrough > 0)
                    {
                        var remaining = responseLength - forwardedThrough;
                        Buffer.BlockCopy(response, forwardedThrough, response, 0, remaining);
                        pendingResponse.SetLength(remaining);
                        pendingResponse.Position = remaining;
                    }
                }
                else
                {
                    await client.GetStream().WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
        }

        private static List<BackendMessage> parseBackendMessages(byte[] response, int responseLength)
        {
            var messages = new List<BackendMessage>();
            var offset = 0;
            while (responseLength - offset >= 5)
            {
                var messageLength = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(offset + 1, 4));
                if (messageLength < 4) throw new InvalidDataException("Invalid PostgreSQL backend message length");

                var totalLength = messageLength + 1;
                if (responseLength - offset < totalLength) break;

                string? commandTag = null;
                if (response[offset] == (byte)'C')
                {
                    commandTag = Encoding.ASCII.GetString(response, offset + 5, messageLength - 4).TrimEnd('\0');
                }

                offset += totalLength;
                messages.Add(new BackendMessage(response[offset - totalLength], commandTag, offset, messageLength));
            }

            return messages;
        }
    }
}

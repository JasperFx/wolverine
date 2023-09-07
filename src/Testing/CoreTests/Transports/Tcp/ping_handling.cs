using System;
using System.Threading;
using System.Threading.Tasks;
using CoreTests.Transports.Tcp.Protocol;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using TestingSupport;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports.Tcp;

public class ping_handling
{
    [Fact]
    public async Task ping_happy_path_with_tcp()
    {
        using (var runtime = WolverineHost.For(opts => { opts.ListenAtPort(2222); }))
        {
            var sender = new BatchedSender(new TcpEndpoint(2222), new SocketSenderProtocol(),
                CancellationToken.None, NullLogger.Instance);

            sender.RegisterCallback(new StubSenderCallback());

            await sender.PingAsync();
        }
    }

    [Fact]
    public async Task ping_sad_path_with_tcp()
    {
        var sender = new BatchedSender(new TcpEndpoint(3322), new SocketSenderProtocol(),
            CancellationToken.None, NullLogger.Instance);

        await Should.ThrowAsync<InvalidOperationException>(async () => { await sender.PingAsync(); });
    }
}
using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Transport;
using Wolverine.Transports;
using Xunit;

namespace PersistenceTests.Transport;

public class database_control_listener_shutdown
{
    /// <summary>
    /// Backport of #4952. The listener sleeps one second between polls on 5.x. That sleep ignored the
    /// listener's cancellation, and StopAsync awaits the polling loop, so stopping a node waited out whatever
    /// was left of the sleep before endpoint draining, ownership release and node deregistration could run.
    /// </summary>
    [Fact]
    public async Task stopping_the_listener_does_not_wait_out_the_poll_delay()
    {
        var database = Substitute.For<IMessageDatabase>();
        database.SchemaName.Returns("control");
        var transport = new DatabaseControlTransport(database, new WolverineOptions());
        var endpoint = (DatabaseControlEndpoint)transport.ControlEndpoint;

        // The receiver never runs the poll batch, so the listener never sees a control message
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = Substitute.For<IReceiver>();
        receiver.ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope>()).Returns(_ =>
        {
            polled.TrySetResult();
            return ValueTask.CompletedTask;
        });

        var listener = new DatabaseControlListener(transport, endpoint, receiver,
            NullLogger<DatabaseControlListener>.Instance, CancellationToken.None);

        // The loop goes straight from this poll into its one second sleep
        await polled.Task.WaitAsync(10.Seconds());

        // Before the fix this took the rest of that second; with it, milliseconds
        var stopping = Stopwatch.StartNew();
        await listener.StopAsync();
        stopping.Stop();

        stopping.Elapsed.ShouldBeLessThan(250.Milliseconds());
    }
}

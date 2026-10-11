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
    /// Once the control queue has been quiet for ten seconds the listener sleeps five seconds between polls.
    /// That sleep ignored the listener's cancellation, and StopAsync awaits the polling loop, so stopping a
    /// quiet node waited out whatever was left of the sleep before endpoint draining, ownership release and
    /// node deregistration could run. A host with a short shutdown window (Azure App Service kills the
    /// container five seconds after SIGTERM) could be killed before it removed its node record.
    /// </summary>
    [Fact]
    public async Task stopping_a_quiet_listener_does_not_wait_out_the_idle_poll_interval()
    {
        var database = Substitute.For<IMessageDatabase>();
        database.SchemaName.Returns("control");
        var transport = new DatabaseControlTransport(database, new WolverineOptions());
        var endpoint = (DatabaseControlEndpoint)transport.ControlEndpoint;

        // The receiver never runs the poll batch, so the listener never sees a control message and stays idle
        var quietFor = new Stopwatch();
        var polledWhileIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = Substitute.For<IReceiver>();
        receiver.ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope>()).Returns(_ =>
        {
            if (quietFor.Elapsed > 10250.Milliseconds())
            {
                polledWhileIdle.TrySetResult();
            }

            return ValueTask.CompletedTask;
        });

        quietFor.Start();
        var listener = new DatabaseControlListener(transport, endpoint, receiver,
            NullLogger<DatabaseControlListener>.Instance, CancellationToken.None);

        // The loop goes straight from this poll into the five second idle sleep
        await polledWhileIdle.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        var stopping = Stopwatch.StartNew();
        await listener.StopAsync();
        stopping.Stop();

        stopping.Elapsed.ShouldBeLessThan(1.Seconds());
    }
}

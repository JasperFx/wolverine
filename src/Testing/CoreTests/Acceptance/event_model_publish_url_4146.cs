using System.Net;
using System.Text;
using JasperFx.Events.EventModeling;
using Shouldly;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace CoreTests.Acceptance;

// GH-4146: `event-model --url <monitor>` PUTs the assembled descriptor instead of (or as well as) writing
// the file, so the design-time loop collapses to `dotnet watch run -- event-model --url ...`.
public class event_model_publish_url_4146
{
    // ---- flag composition (no host, no socket) ----

    [Fact]
    public void neither_flag_still_writes_the_default_file()
    {
        new EventModelInput().ResolveJsonPath().ShouldBe(EventModelInput.DefaultJsonFile);
    }

    [Fact]
    public void json_alone_writes_the_named_file()
    {
        new EventModelInput { JsonFlag = "custom.json" }.ResolveJsonPath().ShouldBe("custom.json");
    }

    [Fact]
    public void url_alone_publishes_without_dropping_a_file()
    {
        // The point of the default moving off "event-model.json": running the watch loop should not
        // litter the application directory with a file nobody asked for.
        new EventModelInput { UrlFlag = "http://localhost:5525" }.ResolveJsonPath().ShouldBeNull();
    }

    [Fact]
    public void json_and_url_compose()
    {
        new EventModelInput { JsonFlag = "custom.json", UrlFlag = "http://localhost:5525" }
            .ResolveJsonPath().ShouldBe("custom.json");
    }

    // ---- the PUT itself, against a real socket ----

    [Fact]
    public async Task publishes_the_same_json_the_file_form_writes()
    {
        await using var monitor = new StubMonitor();

        var model = new EventModelDescriptor("PublishMe", []);
        var succeeded = await invokePublishAsync(model, monitor.Url);

        succeeded.ShouldBeTrue();
        monitor.Method.ShouldBe("PUT");
        monitor.ContentType.ShouldStartWith("application/json");

        // Byte-for-byte the file form's payload, so a monitor cannot tell the two apart.
        monitor.Body.ShouldBe(WolverineEventModelExport.ToJson(model));

        // ...and it round-trips back through the descriptor.
        WolverineEventModelExport.FromJson(monitor.Body!)!.Name.ShouldBe("PublishMe");
    }

    [Fact]
    public async Task a_monitor_that_rejects_the_model_fails_rather_than_reporting_success()
    {
        await using var monitor = new StubMonitor(HttpStatusCode.BadRequest, "not today");

        var succeeded = await invokePublishAsync(new EventModelDescriptor("Rejected", []), monitor.Url);

        succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task a_monitor_that_is_down_fails_with_a_message_not_a_stack_trace()
    {
        // Nothing is listening here. Under `dotnet watch` a monitor that has not been started yet is the
        // ordinary case, so this has to be a sentence and a non-zero exit -- never an unhandled exception.
        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);

        bool succeeded;
        try
        {
            succeeded = await invokePublishAsync(new EventModelDescriptor("Nobody", []),
                new Uri($"http://127.0.0.1:{unusedPort()}"));
        }
        finally
        {
            Console.SetOut(original);
        }

        succeeded.ShouldBeFalse();
        output.ToString().ShouldContain("Could not reach the monitor");
    }

    private static Task<bool> invokePublishAsync(EventModelDescriptor model, Uri monitor)
    {
        var method = typeof(EventModelCommand)
            .GetMethod("publishAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return (Task<bool>)method.Invoke(null, [model, monitor, "the Event Model"])!;
    }

    private static int unusedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Minimal HttpListener standing in for a Bobcat/CritterWatch console.</summary>
    private sealed class StubMonitor : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;

        public StubMonitor(HttpStatusCode status = HttpStatusCode.NoContent, string? responseBody = null)
        {
            var port = unusedPort();
            Url = new Uri($"http://127.0.0.1:{port}/event-model");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            _serving = Task.Run(async () =>
            {
                var context = await _listener.GetContextAsync();
                Method = context.Request.HttpMethod;
                ContentType = context.Request.ContentType;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    Body = await reader.ReadToEndAsync();
                }

                context.Response.StatusCode = (int)status;
                if (responseBody != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(responseBody);
                    await context.Response.OutputStream.WriteAsync(bytes);
                }

                context.Response.Close();
            });
        }

        public Uri Url { get; }
        public string? Method { get; private set; }
        public string? ContentType { get; private set; }
        public string? Body { get; private set; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _serving.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // the negative tests never send a request; nothing to drain
            }

            _listener.Close();
        }
    }
}

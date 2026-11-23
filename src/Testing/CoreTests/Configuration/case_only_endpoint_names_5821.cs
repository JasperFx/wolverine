using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.SharedMemory;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-5821. Wolverine identifies an endpoint by its Uri, and System.Uri lower cases the host. On a transport that puts
/// the endpoint name in the host -- shared memory topics, the stub transport, SQS queues, SNS topics -- two endpoints
/// whose names differ only in case become two endpoint objects with one identity, and anything keyed on the Uri, like
/// the sending agent cache, quietly treats them as one. Wolverine 6.x now warns about such a pair at startup (7.0 refuses to start).
/// </summary>
public class case_only_endpoint_names_5821
{
    public record CaseOnlyFirstMessage;

    public record CaseOnlySecondMessage;

    [Fact]
    public async Task two_shared_memory_topics_whose_names_differ_only_in_case_are_warned_about_and_the_host_starts()
    {
        var logs = new CapturedWarnings5821();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishMessage<CaseOnlyFirstMessage>().ToSharedMemoryTopic("Orders5821");

                // Only one side carries a publishing rule. With a rule on both, the conflation itself already fails
                // startup on 6.x after this warning: the second endpoint never gets a sending agent of its own
                opts.Transports.GetOrCreate<SharedMemoryTransport>().Topics["orders5821"].Subscriptions.Count.ShouldBe(0);
            }).StartAsync(TestContext.Current.CancellationToken);

        var warning = logs.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("shared-memory://Orders5821");
        warning.ShouldContain("shared-memory://orders5821");
        warning.ShouldContain("Shared Memory");
        warning.ShouldContain("treating them as one endpoint");
        warning.ShouldContain("Rename one of them");
        warning.ShouldContain("Wolverine 7.0 refuses to start");
    }

    [Fact]
    public async Task two_stub_endpoints_whose_names_differ_only_in_case_are_warned_about_and_the_host_starts()
    {
        var logs = new CapturedWarnings5821();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                var stubs = opts.Transports.GetOrCreate<StubTransport>();
                stubs.Endpoints["Sales"].IsListener = true;
                stubs.Endpoints["sales"].IsListener = true;
            }).StartAsync(TestContext.Current.CancellationToken);

        var warning = logs.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("stub://Sales");
        warning.ShouldContain("stub://sales");
    }

    [Fact]
    public async Task the_same_shared_memory_topic_referenced_twice_with_the_same_name_is_fine()
    {
        var logs = new CapturedWarnings5821();

        using var host = await Host.CreateDefaultBuilder().ConfigureLogging(x => x.AddProvider(logs)).UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.PublishMessage<CaseOnlyFirstMessage>().ToSharedMemoryTopic("Invoices5821");
            opts.PublishMessage<CaseOnlySecondMessage>().ToSharedMemoryTopic("Invoices5821");
        }).StartAsync(TestContext.Current.CancellationToken);

        var topics = host.Services.GetRequiredService<WolverineOptions>().Transports
            .GetOrCreate<SharedMemoryTransport>().Topics
            .Where(x => x.TopicName.Equals("Invoices5821", StringComparison.OrdinalIgnoreCase));

        topics.Count().ShouldBe(1);
        logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task local_queues_are_case_insensitive_by_design_and_are_not_warned_about()
    {
        // LocalTransport folds every queue name to lower case at registration, so Sales and sales really are the
        // same local queue -- one object, one Uri -- and there is nothing ambiguous to warn about.
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.LocalQueue("Sales5821").Sequential();
            opts.LocalQueue("sales5821").MaximumParallelMessages(3);
        }).StartAsync(TestContext.Current.CancellationToken);

        var queues = host.Services.GetRequiredService<WolverineOptions>().Transports
            .GetOrCreate<LocalTransport>().AllQueues()
            .Where(x => x.EndpointName == "sales5821");

        queues.Count().ShouldBe(1);
    }

    [Fact]
    public void a_transport_whose_names_are_case_insensitive_is_not_warned_about()
    {
        // e.g. NServiceBus over SQL Server, whose default collations make Sales and sales one table
        var stubs = new StubTransport();
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Case Insensitive");
        transport.EndpointNamesAreCaseSensitive.Returns(false);
        transport.Endpoints().Returns([stubs.Endpoints["Sales"], stubs.Endpoints["sales"]]);

        EndpointNameCollisions.Find(transport).ShouldBeEmpty();

        transport.EndpointNamesAreCaseSensitive.Returns(true);
        EndpointNameCollisions.Find(transport).ShouldHaveSingleItem().ShouldContain("Case Insensitive");
    }

    [Fact]
    public async Task a_single_mixed_case_shared_memory_topic_is_one_endpoint()
    {
        // ToSharedMemoryTopic() hands the topic's own Uri back to the transport, which used to look it up by the
        // lower cased Uri host and create a second, lower case topic for the publishing rule to land on
        using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.PublishMessage<CaseOnlyFirstMessage>().ToSharedMemoryTopic("Payments5821");
        }).StartAsync(TestContext.Current.CancellationToken);

        var transport = host.Services.GetRequiredService<WolverineOptions>().Transports
            .GetOrCreate<SharedMemoryTransport>();

        transport.Topics.Where(x => x.TopicName.Equals("Payments5821", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.TopicName).ShouldBe(["Payments5821"]);

        var runtime = host.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>();
        runtime.RoutingFor(typeof(CaseOnlyFirstMessage)).RouteForSend(new CaseOnlyFirstMessage(), null)
            .Single().Sender!.Endpoint.ShouldBeSameAs(transport.Topics["Payments5821"]);
    }

    [Theory]
    [InlineData("sqs://Orders", "Orders")]
    [InlineData("shared-memory://Orders/Sub", "Orders")]
    [InlineData("nsbpostgres://Sales.Endpoint", "Sales.Endpoint")]
    [InlineData("grpc://LocalHost:5000", "LocalHost")]
    [InlineData("rabbitmq://queue/Orders", "queue")]
    public void original_host_keeps_its_case(string uri, string expected)
    {
        EndpointNameCollisions.OriginalHost(new Uri(uri)).ShouldBe(expected);
    }
}

/// <summary>
/// Collects the GH-5821 warnings a host logs, so a test can assert the host started AND said why.
/// </summary>
public class CapturedWarnings5821 : ILoggerProvider
{
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_warnings) return _warnings.Where(x => x.Contains("differ only in case")).ToArray();
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private class Logger(CapturedWarnings5821 parent) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            lock (parent._warnings) parent._warnings.Add(formatter(state, exception));
        }
    }
}

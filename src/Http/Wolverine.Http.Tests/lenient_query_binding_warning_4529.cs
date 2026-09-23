using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Http;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-4529. RejectUnparseableQueryValues defaults to false, so '?page=abc' on an int parameter binds 0 and
/// the request proceeds with nothing logged -- the single most surprising runtime behaviour in
/// Wolverine.HTTP for anyone arriving from MVC or minimal APIs, both of which reject it. Until the default
/// flips in Wolverine 7, say so once at startup.
/// </summary>
public class lenient_query_binding_warning_4529
{
    [Fact]
    public async Task warns_once_when_the_flag_is_off_and_a_parsed_query_parameter_exists()
    {
        var logs = new CapturingLoggerProvider();

        using var host = await startHostAsync(logs, rejectUnparseableQueryValues: false);

        await host.Scenario(x => x.Get.Url("/gh4529/paged?page=3"));

        var warnings = logs.Warnings.Where(x => x.Contains("RejectUnparseableQueryValues")).ToArray();

        // once, not once per endpoint
        warnings.ShouldHaveSingleItem();
        warnings[0].ShouldContain("?page=abc");
        warnings[0].ShouldContain("Wolverine 7");
    }

    [Fact]
    public async Task does_not_warn_when_the_flag_is_on()
    {
        var logs = new CapturingLoggerProvider();

        using var host = await startHostAsync(logs, rejectUnparseableQueryValues: true);

        await host.Scenario(x => x.Get.Url("/gh4529/paged?page=3"));

        logs.Warnings.ShouldNotContain(x => x.Contains("RejectUnparseableQueryValues"));
    }

    [Fact]
    public async Task the_lenient_behaviour_itself_is_unchanged()
    {
        var logs = new CapturingLoggerProvider();

        using var host = await startHostAsync(logs, rejectUnparseableQueryValues: false);

        // Still binds the default and proceeds -- this PR only makes the choice visible, it does not
        // change what the choice does.
        var response = await host.Scenario(x =>
        {
            x.Get.Url("/gh4529/paged?page=abc");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("page 0");
    }

    private static async Task<IAlbaHost> startHostAsync(CapturingLoggerProvider logs, bool rejectUnparseableQueryValues)
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.MediatorOnly;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(lenient_query_binding_warning_4529).Assembly);
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.RejectUnparseableQueryValues = rejectUnparseableQueryValues;

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the GH-4529 endpoint", type => type != typeof(Gh4529Endpoint)));
        }));
    }
}

public static class Gh4529Endpoint
{
    [WolverineGet("/gh4529/paged")]
    public static string Paged(int page) => $"page {page}";
}

internal class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _warnings = [];
    private readonly object _lock = new();

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_lock) return _warnings.ToArray();
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private void capture(string message)
    {
        lock (_lock) _warnings.Add(message);
    }

    private class CapturingLogger(CapturingLoggerProvider parent) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                parent.capture(formatter(state, exception));
            }
        }
    }
}

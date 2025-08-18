using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests;

public class disposing_disposable_or_async_disposable 
{

    [Fact]
    public async Task run_end_to_end()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddScoped<IDisposedService, DisposedService>();
                opts.Services.AddScoped<IAsyncDisposedService, AsyncDisposedService>();
                opts.Services.AddScoped<INotDisposed, NotDisposed>();
            }).StartAsync();
        
        await host.InvokeAsync(new DisposingMessage());
        
        DisposedService.WasDisposed.ShouldBeTrue();
        AsyncDisposedService.WasDisposed.ShouldBeTrue();
    }
}

public record DisposingMessage;

public static class DisposingMessageHandler
{
    public static void Handle(DisposingMessage message, IDisposedService service1, IAsyncDisposedService service2, INotDisposed notDisposed)
    {
        // nothing
    }
}

public interface INotDisposed;

public class NotDisposed : INotDisposed;
public interface IDisposedService;
public interface IAsyncDisposedService;

public class DisposedService : IDisposedService, IDisposable
{
    public void Dispose()
    {
        WasDisposed = true;
    }

    public static bool WasDisposed { get; set; }
}

public class AsyncDisposedService : IAsyncDisposedService, IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        WasDisposed = true;
        return new ValueTask();
    }

    public static bool WasDisposed { get; set; }
}
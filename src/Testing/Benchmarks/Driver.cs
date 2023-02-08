using System;
using System.IO;
using System.Threading.Tasks;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Oakton.Resources;
using TestMessages;
using Wolverine;
using Wolverine.Persistence.Durability;

namespace Benchmarks;

public class Driver : IDisposable
{
    private Task _waiter;

    public Driver()
    {
        var json = File.ReadAllText("targets.json");
        Targets = JsonConvert.DeserializeObject<Target[]>(json);
    }

    public Target[] Targets { get; }

    public IHost Host { get; private set; }

    public IMessageBus Bus { get; private set; }

    public IMessageStore Persistence { get; private set; }

    public void Dispose()
    {
        Host?.Dispose();
    }

    public async Task Start(Action<WolverineOptions> configure)
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                configure(opts);
                opts.Durability.CodeGeneration.ApplicationAssembly = GetType().Assembly;
                opts.Durability.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Error);
            })
            .StartAsync();

        Persistence = Host.Services.GetRequiredService<IMessageStore>();
        Bus = Host.Services.GetRequiredService<IMessageBus>();

        await Host.ResetResourceState();

        _waiter = TargetHandler.WaitForNumber(Targets.Length, 60.Seconds());
    }

    public Task WaitForAllEnvelopesToBeProcessed()
    {
        return _waiter;
    }

    public T Get<T>()
    {
        return Host.Services.GetRequiredService<T>();
    }

    public async Task Teardown()
    {
        if (Host != null)
        {
            await Host.StopAsync();
            Host = null;
        }
    }
}
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace Wolverine.FluentValidation.Tests;

public class end_to_end
{
    [Fact]
    public async Task invoke_happy_path_with_multiple_validators()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseFluentValidation();

                opts.Services.AddScoped<IDataService, DataService>();
            }).StartAsync();


        var command = new Command1
        {
            Name = "foo", Color = "blue", Number = 4
        };

        var session = await host.InvokeMessageAndWaitAsync(command);
        session.Executed.SingleMessage<Command1>().ShouldBeSameAs(command);
    }

    [Fact]
    public async Task invoke_sad_path_with_multiple_validators()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseFluentValidation();

                opts.Services.AddScoped<IDataService, DataService>();
            }).StartAsync();


        var command = new Command1
        {
            Name = null, Color = "blue", Number = 4
        };

        await Should.ThrowAsync<ValidationException>(() => host.InvokeAsync(command));
    }

    [Fact]
    public async Task invoke_happy_path_with_single_validator()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseFluentValidation();

                opts.Services.AddScoped<IDataService, DataService>();
            }).StartAsync();


        var command = new Command2
        {
            Name = "foo", Color = "blue"
        };

        var session = await host.InvokeMessageAndWaitAsync(command);
        session.Executed.SingleMessage<Command2>().ShouldBeSameAs(command);
    }

    [Fact]
    public async Task invoke_sad_path_with_single_validator()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseFluentValidation();

                opts.Services.AddScoped<IDataService, DataService>();
            }).StartAsync();


        var command = new Command2
        {
            Name = null
        };

        await Should.ThrowAsync<ValidationException>(() => host.InvokeAsync(command));
    }
}
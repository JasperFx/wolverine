using Microsoft.Extensions.Hosting;
using Wolverine.DataAnnotationsValidation.Internals;
using Wolverine.Tracking;

namespace Wolverine.DataAnnotationsValidation.Tests;

public class end_to_end
{
    [Fact]
    public async Task invoke_happy_path_with_multiple_validators()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseDataAnnotationsValidation();
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
                opts.UseDataAnnotationsValidation();
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
                opts.UseDataAnnotationsValidation();
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
                opts.UseDataAnnotationsValidation();
            }).StartAsync();

        var command = new Command2
        {
            Name = null
        };

        await Should.ThrowAsync<ValidationException>(() => host.InvokeAsync(command));
    }

    [Fact]
    public async Task invoke_sad_path_validator_with_async_rule()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseDataAnnotationsValidation();
            }).StartAsync();

        var command = new Command4
        {
            Email = "existing@email.me"
        };

        await Should.ThrowAsync<ValidationException>(() => host.InvokeAsync(command));
        await host.StopAsync();
    }

    [Fact]
    public async Task invoke_happy_path_validator_with_async_rule()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseDataAnnotationsValidation();
            }).StartAsync();

        var command = new Command4
        {
            Email = "new@email.me"
        };

        await Should.NotThrowAsync(() => host.InvokeAsync(command));
        await host.StopAsync();
    }
}
using System.Diagnostics;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Configuration;

public class configuring_idempotency_style
{
    [Fact]
    public async Task transactional_middleware_overrides_if_it_has_explicit_value()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        // Forces the codegen rules to be applied that will execute
        // the transactional attribute among other things
        await host.InvokeAsync(new DoSomething(Guid.NewGuid()));
        await host.InvokeAsync(new TM1(Guid.NewGuid()));
        await host.InvokeAsync(new TM2(Guid.NewGuid()));
        await host.InvokeAsync(new TM3(Guid.NewGuid()));
        await host.InvokeAsync(new TM4(Guid.NewGuid()));

        
        var runtime = host.GetRuntime();
        runtime.Handlers.ChainFor<DoSomething>().Idempotency.ShouldBe(IdempotencyStyle.Eager);
        runtime.Handlers.ChainFor<TM4>().Idempotency.ShouldBe(IdempotencyStyle.Optimistic);
        
        runtime.Handlers.ChainFor<TM1>().Idempotency.ShouldBe(IdempotencyStyle.None);
        runtime.Handlers.ChainFor<TM2>().Idempotency.ShouldBe(IdempotencyStyle.None);
        runtime.Handlers.ChainFor<TM3>().Idempotency.ShouldBe(IdempotencyStyle.None);
    }

    [Fact]
    public async Task use_transactional_policies_to_eager()
    {
        #region sample_setting_default_idempotency_check_level

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.AutoApplyTransactions(IdempotencyStyle.Eager);
            })
            .StartAsync();

            #endregion
        
        // Forces the codegen rules to be applied that will execute
        // the transactional attribute among other things
        await host.InvokeAsync(new DoSomething(Guid.NewGuid()));
        await host.InvokeAsync(new TM1(Guid.NewGuid()));
        await host.InvokeAsync(new TM2(Guid.NewGuid()));
        await host.InvokeAsync(new TM3(Guid.NewGuid()));
        await host.InvokeAsync(new TM4(Guid.NewGuid()));
        
        var runtime = host.GetRuntime();
        
        // Just seeing that this caught
        runtime.Handlers.ChainFor<DoSomething>().IsTransactional.ShouldBeTrue();
        
        runtime.Handlers.ChainFor<DoSomething>().Idempotency.ShouldBe(IdempotencyStyle.Eager);
        
        // Override by transactional attribute!
        runtime.Handlers.ChainFor<TM4>().Idempotency.ShouldBe(IdempotencyStyle.Optimistic);
        
        runtime.Handlers.ChainFor<TM1>().Idempotency.ShouldBe(IdempotencyStyle.Eager);
        runtime.Handlers.ChainFor<TM2>().Idempotency.ShouldBe(IdempotencyStyle.Eager);
        runtime.Handlers.ChainFor<TM3>().Idempotency.ShouldBe(IdempotencyStyle.Eager);
    }
    
    
    [Fact]
    public async Task use_transactional_policies_to_optimistic()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.AutoApplyTransactions(IdempotencyStyle.Optimistic);
            })
            .StartAsync();
        
        // Forces the codegen rules to be applied that will execute
        // the transactional attribute among other things
        await host.InvokeAsync(new DoSomething(Guid.NewGuid()));
        await host.InvokeAsync(new TM1(Guid.NewGuid()));
        await host.InvokeAsync(new TM2(Guid.NewGuid()));
        await host.InvokeAsync(new TM3(Guid.NewGuid()));
        await host.InvokeAsync(new TM4(Guid.NewGuid()));
        
        var runtime = host.GetRuntime();
        runtime.Handlers.ChainFor<DoSomething>().Idempotency.ShouldBe(IdempotencyStyle.Eager);
        
        // Override by transactional attribute!
        runtime.Handlers.ChainFor<TM4>().Idempotency.ShouldBe(IdempotencyStyle.Optimistic);
        
        runtime.Handlers.ChainFor<TM1>().Idempotency.ShouldBe(IdempotencyStyle.Optimistic);
        runtime.Handlers.ChainFor<TM2>().Idempotency.ShouldBe(IdempotencyStyle.Optimistic);
        runtime.Handlers.ChainFor<TM3>().Idempotency.ShouldBe(IdempotencyStyle.Optimistic);
    }
}

public record DoSomething(Guid Id);

public static class DoSomethingHandler
{
    #region sample_using_explicit_idempotency_on_single_handler

    [Transactional(IdempotencyStyle.Eager)]
    public static void Handle(DoSomething msg)
    {
        
    }

    #endregion

    public static void Handle(TM1 m) => Debug.WriteLine("Got TM1");
    public static void Handle(TM2 m) => Debug.WriteLine("Got TM2");
    public static void Handle(TM3 m) => Debug.WriteLine("Got TM3");
    
    [Transactional(IdempotencyStyle.Optimistic)]
    public static void Handle(TM4 m) => Debug.WriteLine("Got TM4");
}

public record TM1(Guid Id);

public record TM2(Guid Id);
public record TM3(Guid Id);
public record TM4(Guid Id);

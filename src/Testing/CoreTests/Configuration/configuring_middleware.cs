using System;
using System.Linq;
using System.Threading.Tasks;
using CoreTests.Acceptance;
using JasperFx.CodeGeneration.Frames;
using Microsoft.Extensions.Hosting;
using Wolverine.Middleware;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Configuration;

public class configuring_middleware
{
    [Theory]
    [InlineData(typeof(InvalidBecauseInternal))]
    [InlineData(typeof(InvalidWithNoMatchingMethods))]
    public void invalid_middleware_types(Type middlewareType)
    {
        Should.Throw<InvalidWolverineMiddlewareException>(() =>
        {
            new MiddlewarePolicy().AddType(middlewareType, t => true);
        });
    }

    protected async Task applyMiddleware<T>(Action<HandlerChain> assertions)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.Policies.AddMiddleware<T>(); }).StartAsync();

        var chain = host.GetRuntime().Handlers.ChainFor<MiddlewareMessage>();

        assertions(chain);
    }


    [Fact]
    public async Task simple_before_and_after_with_instance_methods()
    {
        await applyMiddleware<SimpleBeforeAndAfter>(chain =>
        {
            chain.Middleware.FirstOrDefault()
                .ShouldBeOfType<ConstructorFrame>().Variable.VariableType.ShouldBe(typeof(SimpleBeforeAndAfter));

            chain.Middleware[1].ShouldBeCallTo<SimpleBeforeAndAfter>("Before");

            chain.Postprocessors.Last()
                .ShouldBeCallTo<SimpleBeforeAndAfter>("After");
        });
    }

    [Fact]
    public async Task simple_before_and_after_async()
    {
        await applyMiddleware<SimpleBeforeAndAfterAsync>(chain =>
        {
            chain.Middleware.FirstOrDefault()
                .ShouldBeOfType<ConstructorFrame>().Variable.VariableType.ShouldBe(typeof(SimpleBeforeAndAfterAsync));

            chain.Middleware[1].ShouldBeCallTo<SimpleBeforeAndAfterAsync>("BeforeAsync");

            chain.Postprocessors.Last()
                .ShouldBeCallTo<SimpleBeforeAndAfterAsync>("AfterAsync");
        });
    }

    [Fact]
    public async Task find_message_type_of_middleware()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.Policies.AddMiddlewareByMessageType(typeof(MiddlewareWithMessage)); })
            .StartAsync();

        var chain = host.GetRuntime().Handlers.ChainFor<MiddlewareMessage>();
        chain.Middleware[0].ShouldBeOfType<ConstructorFrame>().Variable.VariableType
            .ShouldBe(typeof(MiddlewareWithMessage));
        chain.Middleware[1].ShouldBeCallWithMessageTo(typeof(MiddlewareWithMessage), "Before");

        chain.Postprocessors.Last()
            .ShouldBeCallWithMessageTo(typeof(MiddlewareWithMessage), "After");
    }
}

public abstract class SomeBaseMessage
{
    public int Number { get; set; }
}

public class MiddlewareWithMessage
{
    public void Before(SomeBaseMessage message, Recorder recorder)
    {
    }

    public void After(SomeBaseMessage message)
    {
    }
}

internal static class FrameAssertions
{
    public static void ShouldBeCallTo<T>(this Frame frame, string methodName)
    {
        frame.ShouldBeCallTo(typeof(T), methodName);
    }

    public static void ShouldBeCallTo(this Frame frame, Type middlewareType, string methodName)
    {
        var call = frame.ShouldBeOfType<MethodCall>();

        call.HandlerType.ShouldBe(middlewareType);
        call.Method.Name.ShouldBe(methodName);
    }

    public static void ShouldBeCallWithMessageTo<T>(this Frame frame, string methodName)
    {
        frame.ShouldBeCallWithMessageTo(typeof(T), methodName);
    }

    public static void ShouldBeCallWithMessageTo(this Frame frame, Type middlewareType, string methodName)
    {
        var call = frame.ShouldBeOfType<MethodCallAgainstMessage>();

        call.HandlerType.ShouldBe(middlewareType);
        call.Method.Name.ShouldBe(methodName);
    }
}

public class MiddlewareMessage : SomeBaseMessage
{
}

public class MiddlewareMessageHandler
{
    public void Handle(MiddlewareMessage message)
    {
    }
}

public class SimpleBeforeAndAfter
{
    public void Before()
    {
    }

    public void After()
    {
    }
}

public class SimpleBeforeAndAfterAsync
{
    public Task BeforeAsync()
    {
        return Task.CompletedTask;
    }

    public Task AfterAsync()
    {
        return Task.CompletedTask;
    }
}

internal class InvalidBecauseInternal
{
    public void Before()
    {
    }

    public void After()
    {
    }
}

public class InvalidWithNoMatchingMethods
{
    public void BeforeWrong()
    {
    }

    public void AfterDifferent()
    {
    }
}
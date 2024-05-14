using System.Linq.Expressions;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Wolverine.Runtime.Handlers;

namespace CoreTests.Configuration;

public static class HandlerChainSpecificationExtensions
{
    public static void ShouldHaveHandler<T>(this HandlerChain chain, Expression<Action<T>> expression)
    {
        chain.ShouldNotBeNull();

        var method = ReflectionHelper.GetMethod(expression);
        chain.Handlers.Any(x => x.Method.Name == method.Name).ShouldBeTrue();
    }

    public static void ShouldHaveHandler<T>(this HandlerChain chain, string methodName)
    {
        chain.ShouldNotBeNull();
        chain.Handlers.Any(x => x.Method.Name == methodName && x.HandlerType == typeof(T)).ShouldBeTrue();
    }

    public static void ShouldNotHaveHandler<T>(this HandlerChain chain, Expression<Action<T>> expression)
    {
        if (chain == null)
        {
            return;
        }

        var method = ReflectionHelper.GetMethod(expression);
        chain.Handlers.Any(x => x.Method == method).ShouldBeFalse();
    }

    public static void ShouldNotHaveHandler<T>(this HandlerChain chain, string methodName)
    {
        chain?.Handlers.Any(x => x.Method.Name == methodName).ShouldBeFalse();
    }

    public static void ShouldBeWrappedWith<T>(this HandlerChain chain) where T : Frame
    {
        chain.ShouldNotBeNull();
        chain.Middleware.OfType<T>().Any().ShouldBeTrue();
    }
}
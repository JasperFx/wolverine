using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Middleware;

internal class MiddlewarePolicy : IHandlerPolicy
{
    public static readonly string[] BeforeMethodNames = { "Before", "BeforeAsync" };
    public static readonly string[] AfterMethodNames = { "After", "AfterAsync" };

    private readonly List<Application> _applications = new();

    public void Apply(HandlerGraph graph, GenerationRules rules, IContainer container)
    {
        foreach (var chain in graph.Chains)
        {
            var befores = _applications.SelectMany(x => x.BuildBefore(chain)).ToArray();

            for (var i = 0; i < befores.Length; i++)
            {
                chain.Middleware.Insert(i, befores[i]);
            }

            var afters = _applications.ToArray().Reverse().SelectMany(x => x.BuildAfter(chain));

            chain.Postprocessors.AddRange(afters);
        }
    }

    public Application AddType(Type middlewareType, Func<HandlerChain, bool>? filter = null)
    {
        filter ??= _ => true;
        var application = new Application(middlewareType, filter);
        _applications.Add(application);
        return application;
    }

    public class Application
    {
        private readonly MethodInfo[] _afters;
        private readonly MethodInfo[] _befores;
        private readonly ConstructorInfo? _constructor;

        public Application(Type middlewareType, Func<HandlerChain, bool> filter)
        {
            if (!middlewareType.IsPublic)
            {
                throw new InvalidWolverineMiddlewareException(middlewareType);
            }

            if (!middlewareType.IsStatic())
            {
                var constructors = middlewareType.GetConstructors();
                if (constructors.Length != 1)
                {
                    throw new InvalidWolverineMiddlewareException(middlewareType);
                }

                _constructor = constructors.Single();
            }

            MiddlewareType = middlewareType;
            Filter = filter;

            var methods = middlewareType.GetMethods().ToArray();

            _befores = methods.Where(x => BeforeMethodNames.Contains(x.Name)).ToArray();
            _afters = methods.Where(x => AfterMethodNames.Contains(x.Name)).ToArray();

            if (!_befores.Any() && !_afters.Any())
            {
                throw new InvalidWolverineMiddlewareException(middlewareType);
            }
        }

        public Type MiddlewareType { get; }
        public Func<HandlerChain, bool> Filter { get; }


        public bool MatchByMessageType { get; set; }

        public IEnumerable<Frame> BuildBefore(HandlerChain chain)
        {
            var frames = buildBefores(chain).ToArray();
            if (frames.Any() && !MiddlewareType.IsStatic())
            {
                var constructorFrame = new ConstructorFrame(MiddlewareType, _constructor);
                if (MiddlewareType.CanBeCastTo<IDisposable>() || MiddlewareType.CanBeCastTo<IAsyncDisposable>())
                {
                    constructorFrame.Mode = ConstructorCallMode.UsingNestedVariable;
                }

                yield return constructorFrame;
            }

            foreach (var frame in frames) yield return frame;
        }

        private IEnumerable<Frame> buildBefores(HandlerChain chain)
        {
            if (!Filter(chain))
            {
                yield break;
            }

            foreach (var before in _befores)
            {
                if (MatchByMessageType)
                {
                    var messageType = before.MessageType();
                    if (messageType != null && chain.MessageType.CanBeCastTo(messageType))
                    {
                        var call = new MethodCallAgainstMessage(MiddlewareType, before, messageType);
                        yield return call;

                        if (call.ReturnType == typeof(HandlerContinuation) || call.Creates.Any(x => x.VariableType == typeof(HandlerContinuation)))
                        {
                            yield return new HandlerContinuationFrame(call);
                        }
                    }
                }
                else
                {
                    var call = new MethodCall(MiddlewareType, before);
                    yield return call;

                    if (call.ReturnType == typeof(HandlerContinuation))
                    {
                        yield return new HandlerContinuationFrame(call);
                    }
                }
            }
        }

        public IEnumerable<Frame> BuildAfter(HandlerChain chain)
        {
            var afters = buildAfters(chain).ToArray();

            if (afters.Any() && !MiddlewareType.IsStatic())
            {
                if (chain.Middleware.OfType<ConstructorFrame>().All(x => x.Variable.VariableType != MiddlewareType))
                {
                    yield return new ConstructorFrame(MiddlewareType, _constructor);
                }
            }

            foreach (var after in afters) yield return after;
        }

        private IEnumerable<Frame> buildAfters(HandlerChain chain)
        {
            if (Filter(chain))
            {
                foreach (var after in _afters)
                {
                    if (MatchByMessageType)
                    {
                        var messageType = after.MessageType();
                        if (messageType != null && chain.MessageType.CanBeCastTo(messageType))
                        {
                            yield return new MethodCallAgainstMessage(MiddlewareType, after, messageType);
                        }
                    }
                    else
                    {
                        yield return new MethodCall(MiddlewareType, after);
                    }
                }
            }
        }
    }
}
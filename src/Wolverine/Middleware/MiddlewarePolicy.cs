using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Middleware;

public class MiddlewarePolicy : IChainPolicy
{
    public static readonly string[] BeforeMethodNames = ["Before", "BeforeAsync", "Load", "LoadAsync", "Validate", "ValidateAsync"];
    public static readonly string[] AfterMethodNames = ["After", "AfterAsync", "PostProcess", "PostProcessAsync"];
    public static readonly string[] FinallyMethodNames = ["Finally", "FinallyAsync"];

    private readonly List<Application> _applications = [];

    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var applications = _applications;

        foreach (var chain in chains)
        {
            ApplyToChain(applications, rules, chain);
        }
    }

    public static void AssertMethodDoesNotHaveDuplicateReturnValues(MethodCall call)
    {
        if (call.Method.ReturnType.IsValueType)
        {
            var duplicates = call.Method.ReturnType.GetGenericArguments().GroupBy(x => x).ToArray();
            if (duplicates.Any(x => x.Count() > 1))
            {
                throw new InvalidWolverineMiddlewareException(
                    $"Wolverine middleware cannot support multiple 'creates' variables of the same type. Method {call}, arguments {duplicates.Select(x => x.Key.Name).Join(", ")}");
            }
        }
    }

    internal static void ApplyToChain(List<Application> applications, GenerationRules rules, IChain chain)
    {
        var befores = applications.SelectMany(x => x.BuildBeforeCalls(chain, rules)).ToArray();

        if (chain.InputType() != null &&
            befores.SelectMany(x => x.Creates).Any(x => x.VariableType == chain.InputType()))
        {
            throw new InvalidWolverineMiddlewareException(
                $"It's not currently legal in Wolverine to return the message type for a handler or the request type for an HTTP chain from middleware. Chain: {chain}. If you receive this on the compilation for an HTTP endpoint, you may want to use [NotBody] on the HTTP endpoint parameter so Wolverine will not use that parameter as the request body model");
        }

        for (var i = 0; i < befores.Length; i++)
        {
            chain.Middleware.Insert(i, befores[i]);
        }

        var afters = applications.ToArray().Reverse().SelectMany(x => x.BuildAfterCalls(chain, rules)).ToArray();
        
        if (afters.Any())
        {
            for (int i = 0; i < afters.Length; i++)
            {
                chain.Postprocessors.Insert(i, afters[i]);
            }
            
            //chain.Postprocessors.AddRange(afters);
        }
    }

    public Application AddType(Type middlewareType, Func<IChain, bool>? filter = null)
    {
        filter ??= _ => true;
        var application = new Application(null, middlewareType, filter);
        _applications.Add(application);
        return application;
    }

    public static IEnumerable<MethodInfo> FilterMethods<T>(IChain? chain, IEnumerable<MethodInfo> methods,
        string[] validNames)
        where T : ScopedMiddlewareAttribute
    {
        // MatchesScope watches out for null chain
        return methods
            .Where(x => !x.HasAttribute<WolverineIgnoreAttribute>() && chain!.MatchesScope(x))
            .Where(x => validNames.Contains(x.Name) || x.HasAttribute<T>());
    }

    public class Application
    {
        private readonly IChain? _chain;
        private readonly MethodInfo[] _afters;
        private readonly MethodInfo[] _befores;
        private readonly ConstructorInfo? _constructor;
        private readonly MethodInfo[] _finals;
        
        public Application(IChain? chain, Type middlewareType, Func<IChain, bool> filter)
        {
            if (!middlewareType.IsPublic && !middlewareType.IsVisible)
            {
                throw new InvalidWolverineMiddlewareException(middlewareType);
            }

            _chain = chain;

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

            var methods = middlewareType.GetMethods().Where(x => x.DeclaringType != typeof(object)).ToArray();

            _befores = FilterMethods<WolverineBeforeAttribute>(chain, methods, BeforeMethodNames).ToArray();
            _afters = FilterMethods<WolverineAfterAttribute>(chain, methods, AfterMethodNames).ToArray();
            _finals = FilterMethods<WolverineFinallyAttribute>(chain, methods, FinallyMethodNames).ToArray();

            if (_befores.Length == 0 &&
                _afters.Length == 0 &&
                _finals.Length == 0)
            {
                throw new InvalidWolverineMiddlewareException(middlewareType);
            }
        }

        public Type MiddlewareType { get; }
        public Func<IChain, bool> Filter { get; }

        public bool MatchByMessageType { get; set; }

        public IEnumerable<Frame> BuildBeforeCalls(IChain chain, GenerationRules rules)
        {
            var frames = buildBefores(chain, rules).ToArray();
            if (frames.Length != 0 && !MiddlewareType.IsStatic())
            {
                var constructorFrame = new ConstructorFrame(MiddlewareType, _constructor!);
                if (MiddlewareType.CanBeCastTo<IDisposable>() || MiddlewareType.CanBeCastTo<IAsyncDisposable>())
                {
                    constructorFrame.Mode = ConstructorCallMode.UsingNestedVariable;
                }

                yield return constructorFrame;
            }

            foreach (var frame in frames) yield return frame;
        }

        private IEnumerable<Frame> wrapBeforeFrame(IChain chain, MethodCall call, GenerationRules rules)
        {
            if (_finals.Length == 0)
            {
                yield return call;
                if (rules.TryFindContinuationHandler(chain, call, out var frame))
                {
                    yield return frame!;
                }
            }
            else
            {
                if (rules.TryFindContinuationHandler(chain, call, out var frame))
                {
                    call.Next = frame;
                }

                var finals = _finals.Select(x => new MethodCall(MiddlewareType, x)).OfType<Frame>().ToArray();

                yield return new TryFinallyWrapperFrame(call, finals);
            }
        }

        private MethodCall? buildCallForBefore(IChain chain, MethodInfo before)
        {
            if (MatchByMessageType)
            {
                var inputType = chain.InputType();
                var messageType = before.MessageType();

                if (messageType != null && inputType.CanBeCastTo(messageType))
                {
                    return new MethodCallAgainstMessage(MiddlewareType, before, inputType!);
                }
            }
            else
            {
                return new MethodCall(MiddlewareType, before);
            }

            return null;
        }

        private IEnumerable<Frame> buildBefores(IChain chain, GenerationRules rules)
        {
            if (!Filter(chain))
            {
                yield break;
            }

            if (_befores.Length == 0 &&
                _finals.Length != 0)
            {
                var finals = buildFinals(chain).ToArray();

                yield return new TryFinallyWrapperFrame(new CommentFrame("Wrapped by middleware"), finals);
                yield break;
            }

            foreach (var before in _befores)
            {
                var call = buildCallForBefore(chain, before);
                if (call != null)
                {
                    AssertMethodDoesNotHaveDuplicateReturnValues(call);

                    chain.ApplyParameterMatching(call);

                    foreach (var frame in wrapBeforeFrame(chain, call, rules)) yield return frame;
                }
            }
        }

        private IEnumerable<Frame> buildFinals(IChain chain)
        {
            foreach (var final in _finals)
            {
                if (MatchByMessageType)
                {
                    var messageType = final.MessageType();
                    if (messageType != null && chain.InputType().CanBeCastTo(messageType))
                    {
                        yield return new MethodCallAgainstMessage(MiddlewareType, final, chain.InputType()!);
                    }
                }
                else
                {
                    yield return new MethodCall(MiddlewareType, final);
                }
            }
        }

        public IEnumerable<Frame> BuildAfterCalls(IChain chain, GenerationRules rules)
        {
            var afters = buildAfters(chain).ToArray();

            if (afters.Length != 0 && !MiddlewareType.IsStatic())
            {
                if (chain.Middleware.OfType<ConstructorFrame>().All(x => x.Variable.VariableType != MiddlewareType))
                {
                    yield return new ConstructorFrame(MiddlewareType, _constructor!);
                }
            }

            foreach (var after in afters) yield return after;
        }

        private IEnumerable<Frame> buildAfters(IChain chain)
        {
            if (!Filter(chain))
            {
                yield break;
            }

            foreach (var after in _afters)
            {
                if (MatchByMessageType)
                {
                    var messageType = after.MessageType();
                    if (messageType != null && chain.InputType().CanBeCastTo(messageType))
                    {
                        yield return new MethodCallAgainstMessage(MiddlewareType, after, chain.InputType()!);
                    }
                }
                else
                {
                    var methodCall = new MethodCall(MiddlewareType, after);
                    chain.ApplyParameterMatching(methodCall);
                    yield return methodCall;
                }
            }
        }
    }
}

/// <summary>
/// Wraps a single frame just inside of a dedicated try/finally block, with the
/// "finallys" executed in the finally{} block.
/// </summary>
public class TryFinallyWrapperFrame : Frame
{
    private readonly Frame _inner;
    private readonly Frame[] _finallys;

    public TryFinallyWrapperFrame(Frame inner, Frame[] finallys) : base(inner.IsAsync)
    {
        _inner = inner;
        _finallys = finallys;

        creates.AddRange(inner.Creates.Select(x => new Variable(x.VariableType, x.Usage)));
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        _inner.GenerateCode(method, writer);
        writer.Write("BLOCK:try");

        Next?.GenerateCode(method, writer);

        writer.FinishBlock();
        writer.Write("BLOCK:finally");

        if (_finallys.Length > 1)
        {
            for (var i = 1; i < _finallys.Length; i++)
            {
                _finallys[i - 1].Next = _finallys[i];
            }
        }

        _finallys[0].GenerateCode(method, writer);

        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var variable in _inner.FindVariables(chain))
        {
            yield return variable;
        }

        // NOT letting the finallys get involved with ordering frames
        // because that way lies madness
        foreach (var @finally in _finallys)
        {
            foreach (var variable in @finally.FindVariables(chain))
            {
                yield return variable;
            }
        }
    }
}

using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Codegen;
using Wolverine.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Codegen;

public class BruteForceTests
{
    private readonly ITestOutputHelper _output;
    private readonly ServiceCollection theServices = new();
    private readonly ServiceContainer theGraph;

    public BruteForceTests(ITestOutputHelper output)
    {
        _output = output;
        
        theServices.AddScoped<IWidget, AWidget>();

        theServices.AddSingleton<ISingletonLambda>(x => new SingletonLambda());
        theServices.AddScoped<IScopedLambda>(s => new ScopedLambda());
        theServices.AddTransient<ITransientLambda>(s => new TransientLambda());

        theServices.AddSingleton<IInternalSingleton, InternalSingleton>();
        
        theServices.AddSingleton<IColor>(new Red());

        theServices.AddSingleton(typeof(IGenericScoped<>), typeof(GenericScoped<>));
        theServices.AddSingleton(typeof(IGenericSingleton<>), typeof(GenericSingleton<>));
        
        theServices.AddScoped<IUsesScopedLambda, UsesScopedLambda>();

        theServices.AddScoped<IRule, Rule1>();
        theServices.AddScoped<IRule, Rule2>();
        theServices.AddScoped<IRule, Rule3>();
        theServices.AddScoped<IRule, Rule4>();
        
        /* Use cases:
         * Enumeration of services
         * Guard against bi-directional dependencies
         * support keyed services
         */

        theGraph = new ServiceContainer(theServices, theServices.BuildServiceProvider());
    }
    
    [Theory]
    [MemberData(nameof(GetValidCases))]
    public void could_resolve(Type serviceType)
    {
        theGraph.CouldResolve(serviceType).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(GetValidCases))]
    public void can_create_plan(Type serviceType)
    {
        var plan = theGraph.FindDefault(serviceType, []);
        plan.ShouldNotBeNull();
    }
    
    [Theory]    
    [MemberData(nameof(GetValidCases))]
    public void can_actually_create(Type serviceType)
    {
        var assembly = new GeneratedAssembly(new GenerationRules());

        var constructedType = typeof(ServiceHarness<>).MakeGenericType(serviceType);

        var type = assembly.AddType("ServiceAssertion", constructedType);
        var buildMethod = type.MethodFor("Build");

        var source = new ServiceCollectionServerVariableSource(theGraph);
        source.StartNewType();
        source.StartNewMethod();

        buildMethod.Frames.Code("return {0};", new Use(serviceType));

        var code = assembly.GenerateCode(source);
        
        var compiler = new AssemblyGenerator();
        compiler.ReferenceAssembly(GetType().Assembly);
        var builtAssembly = compiler.Generate(code);
        var builtType = builtAssembly.ExportedTypes.Single();

        var assertion = (IAssert)theGraph.BuildFromType(builtType);
        
        _output.WriteLine(code);
        
        assertion.Assert();
    }
    
    
    
    public static IEnumerable<object[]> GetValidCases()
    {
        return Types().Select(x => new object[] { x });
    }

    public static IEnumerable<Type> Types()
    {
        yield return typeof(IWidget);
        yield return typeof(ISingletonLambda);
        yield return typeof(IScopedLambda);
        yield return typeof(ITransientLambda);
        yield return typeof(IInternalSingleton);
        yield return typeof(IColor);

        yield return typeof(IGenericScoped<string>);
        yield return typeof(IGenericSingleton<string>);

        yield return typeof(IUsesScopedLambda);

        yield return typeof(WidgetHolder);

        yield return typeof(TopThing);

        yield return typeof(SimpleThing);

        yield return typeof(RuleHolder1);
        yield return typeof(RuleHolder2);
    }
}

public interface IAssert
{
    void Assert();
}

public abstract class ServiceHarness<T> : IAssert where T : class
{
    public abstract T Build();

    public void Assert()
    {
        Build().ShouldNotBeNull();
    }
}
using System.Reflection;
using System.Text.Json;
using TestEndpoints;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.RuntimeCompiler;
using Lamar;

namespace Wolverine.Http.Tests;

public class smoke_test_code_generation_of_endpoints_with_no_service_dependencies
{
    public static IEnumerable<object[]> Methods => typeof(FakeEndpoint).GetMethods().Where(x => x.DeclaringType == typeof(FakeEndpoint)).Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(Methods))]
    public void compile_without_error(MethodInfo method)
    {
        var container = new Container(x =>
        {
            x.ForConcreteType<JsonSerializerOptions>().Configure.Singleton();
            x.For<IServiceVariableSource>().Use(c => c.CreateServiceVariableSource()).Singleton();
        });
        
        var parent = new EndpointGraph(new WolverineOptions{ApplicationAssembly = GetType().Assembly}, container);
        parent.Rules.ReferenceAssembly(GetType().Assembly); 
        
        var endpoint = new EndpointChain(new MethodCall(typeof(FakeEndpoint), method), parent);

        
        endpoint.As<ICodeFile>().InitializeSynchronously(parent.Rules, parent, parent.Container);
    }
}
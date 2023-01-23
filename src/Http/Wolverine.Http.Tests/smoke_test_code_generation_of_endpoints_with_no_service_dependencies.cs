using System.Reflection;
using TestEndpoints;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using JasperFx.RuntimeCompiler;

namespace Wolverine.Http.Tests;

public class smoke_test_code_generation_of_endpoints_with_no_service_dependencies
{
    public static IEnumerable<object[]> Methods => typeof(FakeEndpoint).GetMethods().Where(x => x.DeclaringType == typeof(FakeEndpoint)).Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(Methods))]
    public void compile_without_error(MethodInfo method)
    {
        var parent = new EndpointGraph();
        parent.Rules.ReferenceAssembly(GetType().Assembly); 
        
        var endpoint = new EndpointChain(new MethodCall(typeof(FakeEndpoint), method), parent);

        
        endpoint.As<ICodeFile>().InitializeSynchronously(parent.Rules, parent, parent.Container);
    }
}
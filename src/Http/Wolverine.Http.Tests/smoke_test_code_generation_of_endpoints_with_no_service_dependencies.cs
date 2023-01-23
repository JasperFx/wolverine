using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using JasperFx.RuntimeCompiler;

namespace Wolverine.Http.Tests;

public class smoke_test_code_generation_of_endpoints_with_no_service_dependencies
{
    public static IEnumerable<object[]> Methods => typeof(FakeEndpoint).GetMethods().Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(Methods))]
    public void compile_without_error(MethodInfo method)
    {
        var endpoint = new EndpointChain(new MethodCall(typeof(FakeEndpoint), method));

        var parent = new EndpointGraph();
        endpoint.As<ICodeFile>().InitializeSynchronously(parent.Rules, parent, null);
    }
}
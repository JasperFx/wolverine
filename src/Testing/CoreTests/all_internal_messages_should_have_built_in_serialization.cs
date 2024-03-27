using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests;

public class all_internal_messages_should_have_built_in_serialization
{
    [Fact]
    public void check()
    {
        var assembly = typeof(WolverineOptions).Assembly;
        var types = assembly.DefinedTypes
            .Where(x => x.CanBeCastTo<IAgentCommand>()).Where(x => x.IsClass).ToArray();

        var missing = types.Where(x => !x.CanBeCastTo(typeof(ISerializable)));
        if (missing.Any())
        {
            Assert.Fail("Message types are not serializable yet: \n" + missing.Select(x => x.FullName).Join("\n"));
        }
    }
}
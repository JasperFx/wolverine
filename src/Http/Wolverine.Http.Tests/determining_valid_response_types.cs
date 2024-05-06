using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class determining_valid_response_types
{
    [Theory]
    [InlineData(typeof(ItemCreated), true)]
    [InlineData(typeof(string), true)]
    [InlineData(typeof(Events), false)]
    [InlineData(typeof(OutgoingMessages), false)]
    [InlineData(typeof(IAsyncEnumerable<object>), false)]
    [InlineData(typeof(IEnumerable<object>), false)]
    [InlineData(typeof(object[]), false)]
    [InlineData(typeof(SpecialReturnType), false)]
    public void is_valid_response_type(Type type, bool canBeResponse)
    {
        HttpChain.IsValidResponseType(type).ShouldBe(canBeResponse);
    }

    public class SpecialReturnType : IWolverineReturnType;
}
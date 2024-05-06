using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_229_using_generic_types_with_local_queue_names
{
    [Fact]
    public async Task can_start_up_the_app_without_invalid_queueNames_for_message_types()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();
    }
}

public class TestSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public void Start(Result<Something, Error> resultFromAHandler) {}
}
public static class TestHandler
{
    public static void Handle(Result<Something, Error> cascadingMessageFromAnotherHandler)
    {
    }
}

public class Result<T1, T2>
{
    public Guid Id { get; set; }
}

public class Something;
public class Error;
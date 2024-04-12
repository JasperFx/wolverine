using Wolverine;

namespace WolverineWebApi;

public class TestAsyncExtension : IAsyncWolverineExtension
{
    private readonly IConfiguration _configuration;

    public TestAsyncExtension(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ValueTask Configure(WolverineOptions options)
    {
        options.ServiceName = _configuration["ServiceName"]!;
        return new ValueTask();
    }
}
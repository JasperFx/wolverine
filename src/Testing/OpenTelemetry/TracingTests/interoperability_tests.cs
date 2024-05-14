using JasperFx.Core;
using OtelMessages;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace TracingTests;

[Collection("otel")]
public class interoperability_tests : IClassFixture<HostsFixture>
{
    private readonly HostsFixture _fixture;
    private readonly ITestOutputHelper _output;

    public interoperability_tests(HostsFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task invoke_a_message()
    {
        var session = await _fixture.WebApi
            .TrackActivity()
            .AlsoTrack(_fixture.FirstSubscriber)
            .AlsoTrack(_fixture.SecondSubscriber)
            .Timeout(1.Minutes())
            .ExecuteAndWaitAsync(c =>
            {
                return _fixture.WebApi.Scenario(x =>
                {
                    x.WithRequestHeader("X-Correlation-ID", "Lakers");
                    x.Post.Json(new InitialPost("James Worthy")).ToUrl("/invoke");
                });
            });


        foreach (var record in session.AllRecordsInOrder().Where(x => x.MessageEventType == MessageEventType.MessageSucceeded))
            _output.WriteLine(record.ToString());
    }

    [Fact]
    public async Task enqueue_a_message()
    {
        var session = await _fixture.WebApi
            .TrackActivity()
            .AlsoTrack(_fixture.FirstSubscriber)
            .AlsoTrack(_fixture.SecondSubscriber)
            .Timeout(1.Minutes())
            .ExecuteAndWaitAsync(c =>
            {
                return _fixture.WebApi.Scenario(x =>
                {
                    x.Post.Json(new InitialPost("James Worthy")).ToUrl("/enqueue");
                });
            });


        foreach (var record in session.AllRecordsInOrder().Where(x => x.MessageEventType == MessageEventType.MessageSucceeded))
            _output.WriteLine(record.ToString());
    }
}
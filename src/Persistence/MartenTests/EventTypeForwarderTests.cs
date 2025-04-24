using JasperFx.Events;
using Marten.Events;
using Shouldly;
using Wolverine.Marten;

namespace MartenTests;

public class EventWrapperForwarderTests
{
    [Fact]
    public void positive_forward()
    {
        var messageType = typeof(FakeEvent<SecondEvent>);
        var forwarder = new EventWrapperForwarder();
        forwarder.TryFindHandledType(messageType, out var actual)
            .ShouldBeTrue();

        actual.ShouldBe(typeof(IEvent<SecondEvent>));
    }

    [Fact]
    public void negative_forwarding()
    {
        var forwarder = new EventWrapperForwarder();
        forwarder.TryFindHandledType(typeof(SecondEvent), out var actual)
            .ShouldBeFalse();
    }
}

public class FakeEvent<T> : IEvent<T>
{
    public void SetHeader(string key, object value)
    {
        throw new NotImplementedException();
    }

    public object GetHeader(string key)
    {
        throw new NotImplementedException();
    }

    public Guid Id { get; set; }
    public long Version { get; set; }
    public long Sequence { get; set; }
    object IEvent.Data => Data;

    public T Data { get; }
    public Guid StreamId { get; set; }
    public string StreamKey { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string TenantId { get; set; }
    public Type EventType { get; }
    public string EventTypeName { get; set; }
    public string DotNetTypeName { get; set; }
    public string CausationId { get; set; }
    public string CorrelationId { get; set; }
    public Dictionary<string, object> Headers { get; set; }
    public bool IsArchived { get; set; }
    public string AggregateTypeName { get; set; }
}
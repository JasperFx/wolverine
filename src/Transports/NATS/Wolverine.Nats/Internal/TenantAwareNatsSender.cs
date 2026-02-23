using Wolverine.Transports.Sending;

namespace Wolverine.Nats.Internal;

/// <summary>
/// A wrapper around a base NATS sender that applies tenant-specific subject mapping
/// </summary>
internal class TenantAwareNatsSender : ISender
{
    private readonly ISender _innerSender;
    private readonly string _tenantId;
    private readonly ITenantSubjectMapper _subjectMapper;

    public TenantAwareNatsSender(ISender innerSender, string tenantId, ITenantSubjectMapper subjectMapper)
    {
        _innerSender = innerSender ?? throw new ArgumentNullException(nameof(innerSender));
        _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        _subjectMapper = subjectMapper ?? throw new ArgumentNullException(nameof(subjectMapper));
    }

    public Uri Destination => _innerSender.Destination;

    public bool SupportsNativeScheduledSend => _innerSender.SupportsNativeScheduledSend;

    public bool SupportsNativeScheduledCancellation => false;

    public Task<bool> PingAsync() => _innerSender.PingAsync();

    public async ValueTask SendAsync(Envelope envelope)
    {
        var tenantEnvelope = new Envelope(envelope)
        {
            TenantId = _tenantId
        };

        if (tenantEnvelope.Destination != null)
        {
            var originalSubject = NatsTransport.ExtractSubjectFromUri(tenantEnvelope.Destination);
            var tenantSubject = _subjectMapper.MapSubject(originalSubject, _tenantId);
            tenantEnvelope.Destination = new Uri($"nats://subject/{tenantSubject}");
        }

        await _innerSender.SendAsync(tenantEnvelope);
    }
}
namespace Wolverine.Transports.SharedMemory;

internal static class SharedMemoryEnvelope
{
    public static Envelope CopyForDelivery(Envelope source)
    {
        // The sending agent can recycle its envelope as soon as SendAsync returns,
        // while Shared Memory receivers process deliveries asynchronously.
        // Copy the message and transport state without carrying sender/pool ownership.
        var copy = new Envelope
        {
            Message = source.Message,
            Data = source.MessagePayloadSize.HasValue ? source.Data : null,
            Headers = new Dictionary<string, string?>(source.Headers),
            AckRequested = source.AckRequested,
            Attempts = source.Attempts,
            SentAt = source.SentAt,
            Source = source.Source,
            MessageType = source.MessageType,
            ReplyUri = source.ReplyUri,
            ContentType = source.ContentType,
            CorrelationId = source.CorrelationId,
            SagaId = source.SagaId,
            ConversationId = source.ConversationId,
            Destination = source.Destination,
            ParentId = source.ParentId,
            TenantId = source.TenantId,
            UserName = source.UserName,
            AcceptedContentTypes = source.AcceptedContentTypes.ToArray(),
            Id = source.Id,
            ReplyRequested = source.ReplyRequested,
            TopicName = source.TopicName,
            EndpointName = source.EndpointName,
            GroupId = source.GroupId,
            DeduplicationId = source.DeduplicationId,
            PartitionKey = source.PartitionKey,
            ScheduledTime = source.ScheduledTime,
            DeliverBy = source.DeliverBy,
            RoutingInformation = source.RoutingInformation,
            Offset = source.Offset,
            PartitionId = source.PartitionId,
            KeepUntil = source.KeepUntil,
            Serializer = source.Serializer,
            IsResponse = source.IsResponse
        };

        foreach (var header in source.ToMetricsHeaders())
        {
            if (header.Key is MetricsConstants.MessageTypeKey or MetricsConstants.MessageDestinationKey or MetricsConstants.TenantIdKey)
            {
                continue;
            }

            copy.SetMetricsTag(header.Key, header.Value!);
        }

        return copy;
    }
}

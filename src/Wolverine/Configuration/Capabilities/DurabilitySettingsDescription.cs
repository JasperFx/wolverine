namespace Wolverine.Configuration.Capabilities;

public class DurabilitySettingsDescription
{
    public DurabilitySettingsDescription()
    {
    }

    public DurabilitySettingsDescription(DurabilitySettings settings)
    {
        Mode = settings.Mode.ToString();
        DurabilityAgentEnabled = settings.DurabilityAgentEnabled;
        RecoveryBatchSize = settings.RecoveryBatchSize;
        KeepAfterMessageHandling = settings.KeepAfterMessageHandling.ToString();
        ScheduledJobPollingTime = settings.ScheduledJobPollingTime.ToString();
        HealthCheckPollingTime = settings.HealthCheckPollingTime.ToString();
        StaleNodeTimeout = settings.StaleNodeTimeout.ToString();
        CheckAssignmentPeriod = settings.CheckAssignmentPeriod.ToString();
        NodeReassignmentPollingTime = settings.NodeReassignmentPollingTime.ToString();
        MetricsCollectionSamplingInterval = settings.MetricsCollectionSamplingInterval.ToString();
        DeadLetterQueueExpirationEnabled = settings.DeadLetterQueueExpirationEnabled;
        DeadLetterQueueExpiration = settings.DeadLetterQueueExpiration.ToString();
        NodeEventRecordExpirationTime = settings.NodeEventRecordExpirationTime.ToString();
        SendingAgentIdleTimeout = settings.SendingAgentIdleTimeout.ToString();
        DurabilityMetricsEnabled = settings.DurabilityMetricsEnabled;
        OutboxStaleTime = settings.OutboxStaleTime?.ToString();
        InboxStaleTime = settings.InboxStaleTime?.ToString();
        MessageIdentity = settings.MessageIdentity.ToString();
    }

    public string Mode { get; set; } = "Balanced";
    public bool DurabilityAgentEnabled { get; set; } = true;
    public int RecoveryBatchSize { get; set; } = 100;
    public string KeepAfterMessageHandling { get; set; } = "00:05:00";
    public string ScheduledJobPollingTime { get; set; } = "00:00:05";
    public string HealthCheckPollingTime { get; set; } = "00:00:10";
    public string StaleNodeTimeout { get; set; } = "00:01:00";
    public string CheckAssignmentPeriod { get; set; } = "00:00:30";
    public string NodeReassignmentPollingTime { get; set; } = "00:00:05";
    public string MetricsCollectionSamplingInterval { get; set; } = "00:00:05";
    public bool DeadLetterQueueExpirationEnabled { get; set; }
    public string DeadLetterQueueExpiration { get; set; } = "10.00:00:00";
    public string NodeEventRecordExpirationTime { get; set; } = "5.00:00:00";
    public string SendingAgentIdleTimeout { get; set; } = "00:05:00";
    public bool DurabilityMetricsEnabled { get; set; } = true;
    public string? OutboxStaleTime { get; set; }
    public string? InboxStaleTime { get; set; }
    public string MessageIdentity { get; set; } = "IdOnly";
}

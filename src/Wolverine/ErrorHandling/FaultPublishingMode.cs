namespace Wolverine.ErrorHandling;

/// <summary>
/// Per-message-type effective fault-publishing mode after merging the global
/// setting with any explicit per-type override.
/// </summary>
internal enum FaultPublishingMode
{
    None,
    DlqOnly,
    DlqAndDiscard,
}

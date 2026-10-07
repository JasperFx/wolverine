namespace Wolverine.Nats.Configuration;

/// <summary>
/// What Wolverine does at startup with a JetStream stream declared through <c>DefineStream()</c> and friends, and
/// with the named consumer of a JetStream listener, when it already exists on the server. Set it through
/// <c>UseNats(...).Provisioning(...)</c>. When it is not set, existing streams are left alone and named consumers
/// are brought in line with the configuration. Only the settings Wolverine itself writes are compared or changed;
/// everything else on an existing stream or consumer is left as the server has it. <c>resources setup</c> /
/// <c>AddResourceSetupOnStartup()</c> is not affected by this setting.
/// </summary>
public enum NatsProvisioning
{
    /// <summary>
    /// Create a missing stream or consumer and leave an existing one exactly as it is -- also a named consumer,
    /// which is the opt-out for consumers managed outside the application.
    /// </summary>
    CreateOnly,

    /// <summary>
    /// Create a missing stream or consumer, and update an existing one whose settings differ from the
    /// configuration. An update can discard data -- a lower <c>MaxBytes</c> or <c>MaxAge</c> trims the stream --
    /// and the server refuses changes JetStream does not allow on an existing stream or consumer, such as a
    /// different storage type or a change to or from work-queue retention, which then fails the start.
    /// </summary>
    CreateOrUpdate,

    /// <summary>
    /// Create and change nothing. A missing stream or consumer, or one whose settings differ from the
    /// configuration, fails the start with a list of the deviations. For streams and consumers managed outside
    /// the application.
    /// </summary>
    Verify
}

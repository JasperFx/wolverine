namespace Wolverine.Marten;

/// <summary>
/// Public string constants for Marten-specific <see cref="System.Diagnostics.ActivityEvent"/>
/// names emitted by the Wolverine.Marten transactional middleware. These are codegen-time
/// opt-in via <c>WolverineOptions.Tracking.OutboxDiagnosticsEnabled</c>; when the flag is
/// off the bracketing events aren't applied to the <c>SaveChangesAsync</c> postprocessor
/// at all and there is no runtime cost.
/// </summary>
public static class MartenTracing
{
    /// <summary>
    /// ActivityEvent emitted by the codegen wrapper around the Marten
    /// <c>IDocumentSession.SaveChangesAsync(CancellationToken)</c> postprocessor
    /// immediately before the call. Useful for spotting slow transactional commits
    /// when the user's chain pulls in Wolverine.Marten transactional middleware.
    /// </summary>
    public const string MartenSaveChangesStarted = "marten.savechanges.start";

    /// <summary>
    /// ActivityEvent emitted by the codegen wrapper around the Marten
    /// <c>IDocumentSession.SaveChangesAsync(CancellationToken)</c> postprocessor
    /// immediately after the call returns successfully.
    /// </summary>
    public const string MartenSaveChangesFinished = "marten.savechanges.finished";
}

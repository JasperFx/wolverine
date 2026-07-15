namespace Wolverine.Runtime;

/// <summary>
/// Marker interface for a message that is infrastructure/system traffic rather than
/// application activity — e.g. continuously-published monitoring telemetry. A
/// <see cref="Wolverine.Tracking.TrackedSession"/> ignores these by default so a never-ending
/// system feed cannot keep the session from ever completing. A test that specifically wants to
/// assert on system traffic opts it back in with
/// <see cref="Wolverine.Tracking.TrackedSessionConfiguration.IncludeSystemCommands"/>.
///
/// <para>
/// This is orthogonal to <see cref="INotToBeRouted"/>: that marker governs conventional message
/// <em>routing</em>, this one governs test <em>tracking</em>. A message can be one, both, or neither.
/// </para>
/// </summary>
public interface ISystemCommand;

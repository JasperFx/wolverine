// ReSharper disable once CheckNamespace
namespace Wolverine;

/// <summary>
/// GH-5701. Marks a saga method that completes the saga. A message this method returns, such as a trip's
/// <c>TripBooked</c>, is what the saga says when it is finished, not a step that can fail and be compensated, so a
/// catch-all <c>Compensate(object failed, ...)</c> leaves it out.
/// </summary>
/// <remarks>
/// <para>
/// This only tells Wolverine what the method does. The method still calls <c>MarkCompleted()</c> itself.
/// </para>
/// <para>
/// Without it, every message the saga's methods return is covered by a catch-all, completing or not. Name a message
/// with <see cref="CompensatesAttribute" /> to cover it anyway.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CompletesSagaAttribute : Attribute;

using System;

namespace Wolverine.Attributes;

/// <summary>
///     Base class for an attribute that will customize how
///     a message type is sent by Wolverine by modifying the Envelope
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public abstract class ModifyEnvelopeAttribute : Attribute, IEnvelopeRule
{
    public abstract void Modify(Envelope envelope);
}

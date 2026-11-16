using JasperFx.Core.Reflection;

namespace Wolverine.Tracking;

/// <summary>
///     Completes when a message of type <typeparamref name="T" /> reaches one specific
///     <see cref="MessageEventType" /> that <see cref="WaitForMessage{T}" /> does not complete on
/// </summary>
internal class WaitForMessageEventType<T> : ITrackedCondition
{
    private readonly string _description;
    private readonly MessageEventType _eventType;
    private bool _isCompleted;

    public WaitForMessageEventType(MessageEventType eventType, string description)
    {
        _eventType = eventType;
        _description = description;
    }

    public void Record(EnvelopeRecord record)
    {
        if (record.MessageEventType == _eventType && record.Envelope?.Message is T)
        {
            _isCompleted = true;
        }
    }

    public bool IsCompleted() => _isCompleted;

    public override string ToString() => $"Wait for message of type {typeof(T).FullNameInCode()} {_description}";
}

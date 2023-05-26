namespace Wolverine;

/// <summary>
/// Denotes a Handler method that is invalid for Wolverine 
/// </summary>
public class InvalidHandlerException : Exception
{
    public InvalidHandlerException(string? message) : base(message)
    {
    }
}
namespace Wolverine.Persistence;

public enum OnMissing
{
    /// <summary>
    /// Default behavior. In a message handler, the execution will just stop after logging that the data was missing. In an HTTP
    /// endpoint the request will stop w/ an empty body and 404 status code
    /// </summary>
    Simple404,
    
    /// <summary>
    /// In a message handler, the execution will log that the required data is missing and stop execution. In an HTTP
    /// endpoint the request will stop w/ a 400 response and a ProblemDetails body describing the missing data
    /// </summary>
    ProblemDetailsWith400,
    
    /// <summary>
    /// In a message handler, the execution will log that the required data is missing and stop execution. In an HTTP
    /// endpoint the request will stop w/ a 404 status code response and a ProblemDetails body describing the missing data
    /// </summary>
    ProblemDetailsWith404,
    
    /// <summary>
    /// Throws a RequiredDataMissingException using the MissingMessage
    /// </summary>
    ThrowException
}

public class RequiredDataMissingException : Exception
{
    public RequiredDataMissingException(string? message) : base(message)
    {
    }
}

public interface IDataRequirement
{
    bool Required { get; set; }
    string MissingMessage { get; set; }
    OnMissing OnMissing { get; set; }
}
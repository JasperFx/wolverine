namespace Wolverine.Persistence;

public enum MissingDataBehavior
{
    /// <summary>
    /// Default behavior. In a message handler, the execution will just stop after logging that the data was missing. In an HTTP
    /// endpoint the request will stop w/ an empty body and 404 status code
    /// </summary>
    NotFound404,
    
    /// <summary>
    /// In a message handler, the execution will log that the required data is missing and stop execution. In an HTTP
    /// endpoint the request will stop w/ a 400 response and a ProblemDetails body describing the missing data
    /// </summary>
    ProblemDetails400,
    
    /// <summary>
    /// In a message handler, the execution will log that the required data is missing and stop execution. In an HTTP
    /// endpoint the request will stop w/ a 404 response and a ProblemDetails body describing the missing data
    /// </summary>
    NotFoundProblemDetails404,
    
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
    string? NotFoundMessage { get; set; }
    MissingDataBehavior? MissingBehavior { get; set; }
}
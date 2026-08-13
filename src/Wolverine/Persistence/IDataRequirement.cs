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
    ThrowException,

    /// <summary>
    /// In a message handler, the execution will just stop after logging that the data was missing -- identical to
    /// <see cref="Simple404"/>. In an HTTP endpoint the request will stop w/ an empty body and a 204 status code to
    /// denote "the Url was correct, but there is no content." On any GET or QUERY endpoint this value also forces
    /// the data to be treated as required regardless of the <see cref="IDataRequirement.Required"/> setting, because
    /// a 204 is a benign outcome and there is no reason to run the endpoint with a null entity.
    /// </summary>
    EmptyContentWith204
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
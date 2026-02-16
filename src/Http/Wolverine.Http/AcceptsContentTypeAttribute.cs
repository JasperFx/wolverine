namespace Wolverine.Http;

/// <summary>
/// Specify the accepted content types for this endpoint. When multiple endpoints share
/// the same route and HTTP method, Wolverine will use the Content-Type header to select
/// the correct endpoint. This enables API versioning via custom MIME types, e.g.
/// "application/vnd.myapp.v1+json".
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class AcceptsContentTypeAttribute : Attribute
{
    public AcceptsContentTypeAttribute(params string[] contentTypes)
    {
        if (contentTypes.Length == 0)
        {
            throw new ArgumentException("At least one content type must be specified.", nameof(contentTypes));
        }

        ContentTypes = contentTypes;
    }

    public string[] ContentTypes { get; }
}

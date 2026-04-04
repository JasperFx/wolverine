namespace Wolverine.Http.ContentNegotiation;

/// <summary>
/// Marks a method as a response writer for a specific content type.
/// Used for content negotiation — the method will be called when the
/// client's Accept header matches the specified content type.
/// The method should write the response body to HttpContext.Response.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WritesAttribute : Attribute
{
    public WritesAttribute(string contentType)
    {
        ContentType = contentType;
    }

    public string ContentType { get; }
}

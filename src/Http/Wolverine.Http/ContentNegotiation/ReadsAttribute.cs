namespace Wolverine.Http.ContentNegotiation;

/// <summary>
/// Marks a method as a request body reader for a specific content type.
/// Used for content negotiation — the method will be called when the
/// client's Content-Type header matches the specified content type.
/// The method should read from HttpContext.Request and return the deserialized body.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class ReadsAttribute : Attribute
{
    public ReadsAttribute(string contentType)
    {
        ContentType = contentType;
    }

    public string ContentType { get; }
}

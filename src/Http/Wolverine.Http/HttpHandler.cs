using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Wolverine.Http.Runtime;
using Wolverine.Http.Runtime.MultiTenancy;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Wolverine.Http;

public abstract class HttpHandler
{
    private readonly WolverineHttpOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// GH-4528. The non-standard but widely-understood "client closed request" status (nginx), used only for
    /// logging and metrics when the client aborts mid-request -- nothing is actually written to a socket that
    /// is already gone. <see cref="StatusCodes"/> has no constant for it because it is not an IANA code.
    /// </summary>
    public const int ClientClosedRequest = 499;

    // ReSharper disable once PublicConstructorInAbstractClass
    public HttpHandler(WolverineHttpOptions wolverineHttpOptions)
    {
        _options = wolverineHttpOptions;
        _jsonOptions = wolverineHttpOptions.JsonSerializerOptions.Value;
    }

    public ResponseCacheAttribute? Caching { get; set; }
    
    public void WriteCacheControls(HttpContext context, int maxAgeInSeconds, bool noStore)
    {
        context.Response.GetTypedHeaders().CacheControl = new()
        {
            MaxAge = maxAgeInSeconds.Seconds(), NoStore = noStore, 
        };
    }

    public async ValueTask<string?> TryDetectTenantId(HttpContext httpContext)
    {
        var tenantId = await _options.TryDetectTenantId(httpContext);
        if (tenantId != null)
        {
            Activity.Current?.SetTag(MetricsConstants.TenantIdKey, tenantId);
        }

        return tenantId;
    }

    public Task WriteProblems(int statusCode, string message, HttpContext context, object? identity)
    {
        if (identity != null)
        {
            message = message.Replace("{Id}", identity.ToString());
        }
        
        var problems = new ProblemDetails
        {
            Status = statusCode,
            Detail = message
        };

        return Results.Problem(problems).ExecuteAsync(context);
    }

    public Task WriteTenantIdNotFound(HttpContext context)
    {
        return Results.Problem(new ProblemDetails
        {
            Status = 400,
            Detail = TenantIdDetection.NoMandatoryTenantIdCouldBeDetectedForThisHttpRequest
        }).ExecuteAsync(context);
    }

    public abstract Task Handle(HttpContext httpContext);

    public static string? ReadSingleHeaderValue(HttpContext context, string headerKey)
    {
        return context.Request.Headers[headerKey].SingleOrDefault();
    }

    public static string[] ReadManyHeaderValues(HttpContext context, string headerKey)
    {
        return context.Request.Headers[headerKey].ToArray()!;
    }

    public static IFormFile? ReadSingleFormFileValue(HttpContext context)
    {
        return context.Request.Form.Files.SingleOrDefault();
    }

    public static IFormFile? ReadFormFileByName(HttpContext context, string name)
    {
        return context.Request.Form.Files.GetFile(name);
    }

    public static IFormFileCollection? ReadManyFormFileValues(HttpContext context)
    {
        return context.Request.Form.Files;
    }

    /// <summary>
    ///     Opens a <see cref="MultipartReader" /> over the unbuffered request body, with the same limits
    ///     <c>Request.Form</c> would apply: <see cref="FormOptions" />, overridden by the endpoint's
    ///     <see cref="IFormOptionsMetadata" /> (<c>[RequestFormLimits]</c>).
    /// </summary>
    public static async ValueTask<(MultipartReader?, HandlerContinuation)> ReadMultipartAsync(HttpContext context)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType) ||
            !mediaType.MediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return (null, HandlerContinuation.Stop);
        }

        var options = context.RequestServices.GetService<IOptions<FormOptions>>()?.Value ?? new FormOptions();
        var limits = context.GetEndpoint()?.Metadata.GetMetadata<IFormOptionsMetadata>();
        var boundaryLengthLimit = limits?.MultipartBoundaryLengthLimit ?? options.MultipartBoundaryLengthLimit;

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > boundaryLengthLimit)
        {
            await Results.Problem(new()
            {
                Type = "https://httpstatuses.com/400",
                Title = "Invalid multipart boundary",
                Status = StatusCodes.Status400BadRequest,
                Detail = string.IsNullOrWhiteSpace(boundary)
                    ? "The multipart Content-Type header has no boundary."
                    : $"The multipart boundary is longer than the limit of {boundaryLengthLimit} characters.",
                Instance = context.Request.Path
            }).ExecuteAsync(context);

            return (null, HandlerContinuation.Stop);
        }

        var reader = new MultipartReader(boundary, context.Request.Body)
        {
            HeadersCountLimit = limits?.MultipartHeadersCountLimit ?? options.MultipartHeadersCountLimit,
            HeadersLengthLimit = limits?.MultipartHeadersLengthLimit ?? options.MultipartHeadersLengthLimit,
            BodyLengthLimit = limits?.MultipartBodyLengthLimit ?? options.MultipartBodyLengthLimit
        };

        return (reader, HandlerContinuation.Continue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task WriteString(HttpContext context, string? text, int missingStatusCode = 404)
    {
        // A null string resource used to dereference straight into a NullReferenceException below, so a
        // string returning endpoint answered 500 where every other resource type answered 404.
        if (text == null)
        {
            context.Response.StatusCode = missingStatusCode;
            return Task.CompletedTask;
        }

        context.Response.ContentType = "text/plain";
        context.Response.ContentLength = text.Length;
        return context.Response.WriteAsync(text, context.RequestAborted);
    }

    /// <summary>
    /// GH-4547. Arrange for a logical deduplication claim to be released <b>before</b> a failure response
    /// reaches the caller.
    ///
    /// <para>
    /// GH-4501 gave the HTTP chain a compensating release in a <c>finally</c>, keyed on the response
    /// status. That is correct but not atomic with the response: <c>WriteProblems</c> flushes the 404 and
    /// only then does the finally run the DELETE, so a client that retries promptly under the same
    /// <c>Idempotency-Key</c> can beat the release and be told "already handled" for work that never
    /// happened -- the exact failure GH-4501 set out to remove. The window is one database round trip,
    /// which is comfortably inside an automatic retry policy.
    /// </para>
    ///
    /// <para>
    /// <see cref="HttpResponse.OnStarting(Func{Task})" /> callbacks are awaited before the response
    /// headers are flushed, so releasing there closes the window. The generated <c>finally</c> is kept as
    /// well, for the paths where no response ever starts (a throw), and a double release is harmless --
    /// <c>IDeduplicationStore.ReleaseAsync</c> is a DELETE and is documented as idempotent.
    /// </para>
    /// </summary>
    public static void ReleaseDeduplicationClaimBeforeFailureResponse(HttpContext context,
        IMessageDeduplicator deduplicator, string? deduplicationId, Type? ancillaryStoreMarker)
    {
        if (string.IsNullOrWhiteSpace(deduplicationId)) return;

        context.Response.OnStarting(async () =>
        {
            // 400 and up, not "not 2xx" -- a 3xx is an answer, and a POST that redirects to the resource
            // it just created has succeeded exactly once and must keep its claim. Same rule the finally
            // uses, deliberately.
            if (context.Response.StatusCode >= 400)
            {
                await deduplicator
                    .ReleaseAsync(deduplicationId, ancillaryStoreMarker, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        });
    }

    private static readonly object _deduplicatedEndpointRan = new();

    /// <summary>Records that a <c>[Deduplicated]</c> endpoint ran, so its claim is not released as unused.</summary>
    public static void MarkDeduplicatedEndpointRan(HttpContext context)
        => context.Items[_deduplicatedEndpointRan] = true;

    /// <summary>Did the <c>[Deduplicated]</c> endpoint run, rather than middleware ending the request first?</summary>
    public static bool DeduplicatedEndpointRan(HttpContext context)
        => context.Items.ContainsKey(_deduplicatedEndpointRan);

    public void ApplyHttpAware(object target, HttpContext context)
    {
        if (target is IHttpAware a) a.Apply(context);
    }
    
    private const int FingerprintBufferSize = 16 * 1024;

    private static readonly byte[] _fingerprintSeparator = [0];

    // Set once the endpoint's work is done: from then on its [DeduplicatedWithResponse] claim is kept.
    private static readonly object _deduplicatedWorkDone = new();

    /// <summary>
    /// GH-4742. Folds the <see cref="DeduplicationScope" /> parts into a <c>[DeduplicatedWithResponse]</c> key.
    /// Each part is length-prefixed so none can shift another; a missing key passes through so it is still
    /// refused as missing.
    /// </summary>
    public static string? ScopeDeduplicationId(HttpContext context, string? key, DeduplicationScope scope,
        string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(key)) return key;

        var tenant = scope.HasFlag(DeduplicationScope.Tenant) ? tenantId ?? string.Empty : string.Empty;
        var user = scope.HasFlag(DeduplicationScope.User) ? userOf(context) : string.Empty;
        var endpoint = scope.HasFlag(DeduplicationScope.Endpoint) ? endpointOf(context.Request) : string.Empty;

        return $"{tenant.Length}:{tenant}|{user.Length}:{user}|{endpoint.Length}:{endpoint}|{key}";
    }

    // The first non-blank of the name, the name identifier and "sub": JWT bearer often maps no name. Anonymous
    // callers share "".
    private static string userOf(HttpContext context)
    {
        var principal = context.User;
        if (principal?.Identity is not { IsAuthenticated: true } identity) return string.Empty;

        return new[]
               {
                   identity.Name,
                   principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                   principal.FindFirst("sub")?.Value
               }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
               ?? throw new InvalidOperationException(
                   $"[DeduplicatedWithResponse] scopes {context.Request.Method} {context.Request.Path} by user, but the authenticated caller has no name, name identifier or 'sub' claim to scope by, so every such caller would share one scope. See GH-4742");
    }

    /// <summary>
    /// GH-4742. SHA-256 of the method and path, the query string and the body bytes: what was sent, not the
    /// bound object. The endpoint is always included, so a key reused on another endpoint is a different request
    /// even when the scope leaves the endpoint out. Requires a buffered body; null when there is no key.
    /// </summary>
    public static async Task<string?> ComputeDeduplicationFingerprintAsync(HttpContext context,
        string? deduplicationId)
    {
        if (string.IsNullOrWhiteSpace(deduplicationId)) return null;

        var request = context.Request;
        if (!request.Body.CanSeek)
        {
            throw new InvalidOperationException(
                $"The request body for {request.Method} {request.Path} cannot be rewound, so its deduplication fingerprint cannot be computed. Something replaced HttpRequest.Body with a forward-only stream. See GH-4742");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(endpointOf(request)));
        hash.AppendData(_fingerprintSeparator);
        hash.AppendData(Encoding.UTF8.GetBytes(request.QueryString.Value ?? string.Empty));
        hash.AppendData(_fingerprintSeparator);

        var buffer = ArrayPool<byte>.Shared.Rent(FingerprintBufferSize);
        try
        {
            request.Body.Position = 0;
            int read;
            while ((read = await request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            request.Body.Position = 0;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string endpointOf(HttpRequest request)
        => $"{request.Method.ToUpperInvariant()} {request.PathBase}{request.Path}";

    /// <summary>
    /// GH-4742. Answers a request that lost its claim: the stored response when it is a repeat of the same
    /// request, 422 when it is a different one, and 409 while the first is still running.
    /// </summary>
    public async Task AnswerDeduplicatedRepeatAsync(HttpContext context, DeduplicatedResponseClaim claim,
        string? fingerprint, string keyName)
    {
        if (claim.Fingerprint != fingerprint)
        {
            await WriteProblems(StatusCodes.Status422UnprocessableEntity,
                $"This '{keyName}' was already used for a different request; send a new key for a new request",
                context, null).ConfigureAwait(false);
            return;
        }

        if (claim.Response is not { } response)
        {
            await WriteProblems(StatusCodes.Status409Conflict,
                $"A request with this '{keyName}' is still being processed; retry later", context, null)
                .ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = response.StatusCode;

        if (response.Location != null)
        {
            context.Response.Headers.Location = response.Location;
        }

        if (response.Body != null)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// GH-4742. Called once the endpoint and any commit have run: the response about to be written, for the claim
    /// to store, as <see cref="WriteJsonAsync{T}" /> will write it, with the status and <c>Location</c> already set.
    /// Unless that response is a failure (400 and up), the work is done and the claim is kept from here on. Null
    /// records nothing.
    /// </summary>
    public DeduplicatedResponse? CompleteDeduplicatedRequest<T>(HttpContext context, string? deduplicationId,
        T? resource, int missingResourceStatusCode)
    {
        if (string.IsNullOrWhiteSpace(deduplicationId)) return null;

        var status = resource is null ? missingResourceStatusCode : context.Response.StatusCode;
        if (status >= 400) return null;

        context.Items[_deduplicatedWorkDone] = true;

        var location = context.Response.Headers.Location;

        try
        {
            return new DeduplicatedResponse(status,
                resource is null ? null : JsonSerializer.Serialize(resource, _jsonOptions),
                location.Count == 0 ? null : location.ToString());
        }
        catch (Exception e)
        {
            // The work is done, so the claim stays: repeats get 409 until it expires rather than running it again.
            context.RequestServices.GetService<ILogger<DeduplicatedResponses>>()?.LogError(e,
                "Failed to serialize the response for deduplicated id '{DeduplicationId}' to store it", deduplicationId);
            return null;
        }
    }

    /// <summary>GH-4742. Has the endpoint's work been done, so its claim must be kept?</summary>
    public static bool IsDeduplicatedWorkDone(HttpContext context) => context.Items.ContainsKey(_deduplicatedWorkDone);

    /// <summary>
    /// GH-4742. As <see cref="ReleaseDeduplicationClaimBeforeFailureResponse" />, for a
    /// <c>[DeduplicatedWithResponse]</c> claim: a response that starts before the work is done, whatever its
    /// status, gives the claim back before it is flushed.
    /// </summary>
    public static void ReleaseDeduplicatedResponseBeforeFailureResponse(HttpContext context,
        DeduplicatedResponses responses, string? deduplicationId, string claimToken, Type? ancillaryStoreMarker)
    {
        if (string.IsNullOrWhiteSpace(deduplicationId)) return;

        context.Response.OnStarting(async () =>
        {
            if (!IsDeduplicatedWorkDone(context))
            {
                await responses.ReleaseUnansweredAsync(deduplicationId, claimToken, ancillaryStoreMarker).ConfigureAwait(false);
            }
        });
    }

    private static bool isRequestJson(HttpContext context)
    {
        var contentType = context.Request.ContentType;
        if (contentType.IsEmpty())
        {
            return true; // Sure, we'll just go with this.
        }

        if (contentType.StartsWith("application/json") || contentType.StartsWith("text/json") || contentType.Contains("*/*"))
        {
            return true;
        }

        // Support custom MIME types with +json suffix per RFC 6839 (e.g. "application/vnd.myapp.v1+json")
        if (contentType.Contains("+json"))
        {
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<(T?, HandlerContinuation)> ReadJsonAsync<T>(HttpContext context, bool optional = false)
    {
        // An optional body -- a nullable [FromBody] parameter, or a nullable [FromBody] member of an
        // [AsParameters] type -- binds null when the request carries no body, exactly as minimal APIs
        // decide it: the server is asked through IHttpRequestBodyDetectionFeature.CanHaveBody rather than
        // Content-Length, because a chunked or HTTP/2 request carries its body with no Content-Length at
        // all (GH-4935). Content-Length: 0 is kept as the short-circuit for servers without the feature.
        //
        // When the server says there CAN be a body, the body is read, and an empty one fails below as
        // invalid JSON with a 400 -- the same answer minimal APIs give a zero-byte chunked body. There is
        // deliberately no peek-and-forgive here any more (GH-4935, question 1).
        if (optional && (context.Request.ContentLength == 0 ||
                         context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false))
        {
            return (default, HandlerContinuation.Continue);
        }

        try
        {
            var stream = context.Request.Body;

            if (!isRequestJson(context))
            {
                context.Response.StatusCode = 415;
                return (default, HandlerContinuation.Stop);
            }

            if (!acceptsJson(context))
            {
                context.Response.StatusCode = 406;
                return (default, HandlerContinuation.Stop);
            }

            var body = await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions,
                context.RequestAborted);

            return (body, HandlerContinuation.Continue);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // GH-4528: this used to set a 204. A client that disconnects mid-upload then showed as a
            // *successful* no-content response in access logs and metrics, which is how a rash of aborted
            // uploads hides. Nothing is actually sent on an aborted request -- the socket is gone -- so the
            // status here exists only for logging and metrics, and it must not read as success. 499 is the
            // widely-understood "client closed request" convention (nginx, and what most access-log
            // pipelines already bucket separately).
            context.Response.StatusCode = ClientClosedRequest;

            var logger = context.RequestServices.GetService<ILogger<T>>();
            logger?.LogDebug(
                "The client aborted the request to {Url} while Wolverine was reading the JSON body for {Type}",
                context.Request.Path, typeof(T).FullNameInCode());

            return (default, HandlerContinuation.Stop);
        }
        catch (BadHttpRequestException e)
        {
            // GH-4933: the server refused the request body itself before any JSON was parsed: Kestrel's 413 for a
            // body over [RequestSizeLimit] / MaxRequestBodySize, a 400 for a malformed chunk or a truncated body, a
            // 408 for a body that arrives too slowly. Those are client errors that carry their own status, so answer
            // with it, as minimal APIs do. The general catch below would report it as a 500 "server side
            // serialization problem", which sends the client a retryable status for a request that can never
            // succeed, and writes a client-triggerable Error log on every one.
            var logger = context.RequestServices.GetService<ILogger<T>>();
            logger?.LogDebug(e, "The request body at {Url} could not be read for {Type}",
                context.Request.Path, typeof(T).FullNameInCode());

            await Results.Problem(new()
            {
                Type = $"https://httpstatuses.com/{e.StatusCode}",
                Title = "Request body could not be read",
                Status = e.StatusCode,
                Detail = e.Message,
                Instance = context.Request.Path
            }).ExecuteAsync(context);

            return (default, HandlerContinuation.Stop);
        }
        catch (Exception e)
        {
            var logger = context.RequestServices.GetService<ILogger<T>>();
            logger?.LogError(e, "Error trying to deserialize JSON from incoming HTTP body at {Url} to type {Type}",
                context.Request.Path, typeof(T).FullNameInCode());

            if (e is JsonException jsonException)
            {
                await Results.Problem(new()
                {
                    Type = "https://httpstatuses.com/400",
                    Title = "Invalid JSON format",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = jsonException.Message,
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        { "lineNumber", jsonException.LineNumber ?? 0 },
                        { "bytePositionInLine", jsonException.BytePositionInLine ?? 0 }
                    }
                }).ExecuteAsync(context);
            }
            else
            {
                // GH-4528: this used to be a naked `StatusCode = 400` -- no ProblemDetails, no body, no
                // Content-Type. The client learned nothing and the only clue was the server log.
                //
                // It is also the wrong *class* of status. What lands here is not malformed JSON (that is
                // JsonException, above) but a type System.Text.Json cannot handle: a NotSupportedException
                // for a member it cannot deserialize, a throw from a custom JsonConverter, a
                // JsonSerializerOptions mismatch. Those are server-side configuration bugs, and no amount of
                // fixing the request body will help, so answer 500 and say which type and which exception --
                // the two things that actually locate the bug.
                await Results.Problem(new()
                {
                    Type = "https://httpstatuses.com/500",
                    Title = "Request body could not be deserialized",
                    Status = StatusCodes.Status500InternalServerError,
                    Detail =
                        $"The request body could not be deserialized to {typeof(T).FullNameInCode()}. This is a server side serialization problem rather than a malformed request: {e.GetType().Name} was thrown while reading the body. Check the JsonSerializerOptions and any custom JsonConverter registered for this type.",
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        { "exceptionType", e.GetType().FullName },
                        { "targetType", typeof(T).FullNameInCode() }
                    }
                }).ExecuteAsync(context);
            }

            return (default, HandlerContinuation.Stop);
        }
    }

    private static bool acceptsJson(HttpContext context)
    {
        var headers = new RequestHeaders(context.Request.Headers);

        if (!headers.Accept.Any())
        {
            return true;
        }

        return headers.Accept
            .Any(x => x.MediaType is { HasValue: true } &&
                (x.MediaType.Value is "application/json" or "application/problem+json" or "*/*" or "text/json"
                 || x.MediaType.Value!.Contains("+json")));
    }

    /// <summary>
    ///     Write the endpoint's resource as JSON, or -- when it is null -- an empty response with
    ///     <paramref name="missingStatusCode" />. The status code is resolved once at bootstrapping time by
    ///     <c>HttpChain.ResolveMissingResponseBody()</c> and baked into the generated code, so this stays a
    ///     constant on the hot path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task WriteJsonAsync<T>(HttpContext context, T? body, int missingStatusCode = 404)
    {
        if (body == null)
        {
            context.Response.StatusCode = missingStatusCode;
            return Task.CompletedTask;
        }

        return context.Response.WriteAsJsonAsync(body, _jsonOptions, context.RequestAborted);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task WriteProblems(ProblemDetails details, HttpContext context)
    {
        return Results.Problem(details).ExecuteAsync(context);
    }

    /// <summary>
    /// Called by generated code when <see cref="WolverineHttpOptions.RejectUnparseableQueryValues"/>
    /// is enabled and a query string value is present but cannot be parsed to the expected
    /// parameter type. Writes a 400 ProblemDetails response naming the offending query string
    /// parameter, matching ASP.NET Core minimal API binding behavior. GH-3372.
    /// </summary>
    public static Task WriteQueryValueParsingProblem(HttpContext context, string parameterName, string? rawValue,
        string expectedType)
    {
        var details = new ProblemDetails
        {
            Status = 400,
            Title = "Invalid query string value",
            Detail =
                $"Query string parameter '{parameterName}' has the value '{rawValue}' which cannot be parsed to the expected type {expectedType}"
        };

        details.Extensions["parameter"] = parameterName;

        return Results.Problem(details).ExecuteAsync(context);
    }
}
using System.Collections.Immutable;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Wolverine.Attributes;

namespace Wolverine.Http;

/// <summary>
/// Describes a Wolverine HTTP endpoint implementation
/// </summary>
public class WolverineActionDescriptor : ControllerActionDescriptor
{
    public WolverineActionDescriptor(HttpChain chain)
    {
        RouteValues = new Dictionary<string, string?>();
        RouteValues["controller"] = chain.Method.Method.DeclaringType?.FullNameInCode();
        RouteValues["action"] = chain.Method.Method.Name;
        Chain = chain;

        ControllerTypeInfo = chain.Method.HandlerType.GetTypeInfo();

        if (chain.Endpoint != null)
        {
            var metadata = chain.Endpoint!.Metadata.ToList();

            // Ensure summary/description metadata is available to Swashbuckle
            // even if it wasn't in the endpoint metadata collection
            if (chain.EndpointSummary.IsNotEmpty() &&
                !metadata.OfType<IEndpointSummaryMetadata>().Any())
            {
                metadata.Add(new EndpointSummaryAttribute(chain.EndpointSummary));
            }

            if (chain.EndpointDescription.IsNotEmpty() &&
                !metadata.OfType<IEndpointDescriptionMetadata>().Any())
            {
                metadata.Add(new EndpointDescriptionAttribute(chain.EndpointDescription));
            }

            EndpointMetadata = metadata.ToArray();
        }
        else
        {
            // Endpoint may not be built yet when the API description provider runs
            var metadata = new List<object>();

            if (chain.EndpointSummary.IsNotEmpty())
            {
                metadata.Add(new EndpointSummaryAttribute(chain.EndpointSummary));
            }

            if (chain.EndpointDescription.IsNotEmpty())
            {
                metadata.Add(new EndpointDescriptionAttribute(chain.EndpointDescription));
            }

            if (metadata.Count > 0)
            {
                EndpointMetadata = metadata.ToArray();
            }
        }

        ActionName = chain.OperationId;

        MethodInfo = chain.Method.Method;
    }

    public override string? DisplayName
    {
        get => Chain.DisplayName;
        set{}
    }

    /// <summary>
    /// The raw Wolverine model of the HTTP endpoint
    /// </summary>
    public HttpChain Chain { get; }
}

public partial class HttpChain
{
    public ApiDescription CreateApiDescription(string httpMethod)
    {
        var apiDescription = new ApiDescription
        {
            HttpMethod = httpMethod,
            GroupName = Endpoint!.Metadata.GetMetadata<IEndpointGroupNameMetadata>()?.EndpointGroupName,
            RelativePath = Endpoint!.RoutePattern.RawText?.TrimStart('/'),
            ActionDescriptor = new WolverineActionDescriptor(this)
        };
        
        foreach (var routeParameter in RoutePattern!.Parameters)
        {
            var parameter = buildParameterDescription(routeParameter);

            apiDescription.ParameterDescriptions.Add(parameter);
        }

        fillRequestType(apiDescription);

        fillQuerystringParameters(apiDescription);

        fillFormParameters(apiDescription);

        fillKnownHeaderParameters(apiDescription);

        // Query string and header values that are only ever bound by another method in the chain
        // (a compound handler's Load/LoadAsync/Before, an After/Finally postprocessor, a middleware
        // method applied by a policy) are still part of this endpoint's contract. See GH-3380.
        fillChainBoundParameters(apiDescription);

        fillResponseTypes(apiDescription);

        foreach (var parameter in FileParameters)
        {
            var parameterDescription = new ApiParameterDescription
            {
                Name = parameter.Name!,
                ModelMetadata = new EndpointModelMetadata(parameter.ParameterType),
                Source = BindingSource.FormFile,
                ParameterDescriptor = new ParameterDescriptor
                {
                    Name = parameter.Name!,
                    ParameterType = parameter.ParameterType
                },
                Type = parameter.ParameterType,
                IsRequired = true
            };

            apiDescription.ParameterDescriptions.Add(parameterDescription);
        }

        foreach (var formMetadata in Endpoint.Metadata.OfType<IFromFormMetadata>())
        {
            var parameterDescription = new ApiParameterDescription
            {
                Name = formMetadata.Name!,
                ModelMetadata = new EndpointModelMetadata(typeof(IFormFile)),
                Source = BindingSource.Form,
                ParameterDescriptor = new ParameterDescriptor
                {
                    Name = formMetadata.Name!,
                    ParameterType = typeof(IFormFile)
                },
                Type = typeof(IFormFile),
                IsRequired = true
            };

            apiDescription.ParameterDescriptions.Add(parameterDescription);
        }

        // fillRequestType only honors [Consumes] / IAcceptsMetadata when the
        // endpoint has a body request type, is not a form endpoint, and is
        // not a GET (HasRequestType && !IsFormData && HttpMethod != "GET").
        // Without this, form endpoints fall through and ASP.NET Core OpenAPI's
        // GetFormRequestBody defaults SupportedRequestFormats to
        // "application/x-www-form-urlencoded" — silently dropping
        // [Consumes("multipart/form-data")] on file-upload endpoints and
        // causing client generators (Orval, NSwag, Kiota) to emit
        // URLSearchParams bodies instead of multipart.
        if (apiDescription.SupportedRequestFormats.Count == 0 &&
            apiDescription.ParameterDescriptions.Any(p =>
                p.Source == BindingSource.Form || p.Source == BindingSource.FormFile))
        {
            copyAcceptsMetadataToRequestFormats(apiDescription);
        }

        return apiDescription;
    }

    private void copyAcceptsMetadataToRequestFormats(ApiDescription apiDescription)
    {
        foreach (var metadata in Endpoint!.Metadata.OfType<IAcceptsMetadata>())
        {
            foreach (var contentType in metadata.ContentTypes)
            {
                apiDescription.SupportedRequestFormats.Add(new ApiRequestFormat
                {
                    MediaType = contentType
                });
            }
        }
    }

    public override MiddlewareScoping Scoping => MiddlewareScoping.HttpEndpoints;

    public override void UseForResponse(MethodCall methodCall)
    {
        if (methodCall.ReturnVariable == null)
            throw new ArgumentOutOfRangeException(nameof(methodCall),
                $"Method {methodCall} is invalid in this usage. Only a method that returns a single value (not tuples) can be used here.");

        ResourceType = methodCall.ReturnVariable.VariableType;
        
        Postprocessors.Add(methodCall);
        ResourceVariable = methodCall.ReturnVariable;
    }

    public override bool TryFindVariable(string valueName, ValueSource source, Type valueType, out Variable variable)
    {
        // When the endpoint binds a parameter object via [AsParameters], a value that lives on that
        // object must be read off it rather than via the route/query read frames that
        // AsParametersBindingFrame owns and generates inline. Resolving to one of those owned frames
        // (e.g. Marten's [ReadAggregate]/[WriteAggregate] aggregate-id resolution, which searches with
        // ValueSource.Anything) makes the frame get pulled into the method's frame chain a second time,
        // producing a cyclic Next reference and a StackOverflow during code generation. Scoped to
        // ValueSource.Anything so specific-source bindings are unaffected.
        if (source == ValueSource.Anything && AsParametersVariable != null && AsParametersType != null)
        {
            var asParameterMember = (MemberInfo?)AsParametersType.GetProperties()
                    .FirstOrDefault(x => x.Name.EqualsIgnoreCase(valueName) && x.PropertyType == valueType && x.CanRead)
                ?? AsParametersType.GetFields()
                    .FirstOrDefault(x => x.Name.EqualsIgnoreCase(valueName) && x.FieldType == valueType);

            if (asParameterMember != null)
            {
                variable = new MemberAccessVariable(AsParametersVariable, asParameterMember);
                return true;
            }
        }

        if ((source == ValueSource.RouteValue || source == ValueSource.Anything) && FindRouteVariable(valueType, valueName, out variable!))
        {
            return true;
        }

        if ((source == ValueSource.FromQueryString || source == ValueSource.Anything) && FindQuerystringVariable(valueType, valueName, out variable!))
        {
            return true;
        }

        if (source == ValueSource.Header && FindHeaderVariable(valueType, valueName, out variable!))
        {
            return true;
        }

        if (source == ValueSource.Claim && FindClaimVariable(valueType, valueName, out variable!))
        {
            return true;
        }

        if (source == ValueSource.Method)
        {
            return tryFindMethodVariable(valueName, valueType, out variable!);
        }

        if (HasRequestType)
        {
            var requestType = InputType()!;
            var member = requestType.GetProperties()
                             .FirstOrDefault(x => x.Name.EqualsIgnoreCase(valueName) && x.PropertyType == valueType)
                         ?? (MemberInfo?)requestType.GetFields()
                             .FirstOrDefault(x => x.Name.EqualsIgnoreCase(valueName) && x.FieldType == valueType);

            if (member != null)
            {
                if (RequestBodyVariable == null)
                    throw new InvalidOperationException(
                        "Requesting member access to the request body, but the request body is not (yet) set.");

                variable = new MemberAccessVariable(RequestBodyVariable, member);
                return true;
            }
        }

        variable = default!;
        return false;
    }

    internal bool FindHeaderVariable(Type valueType, string headerName, out Variable variable)
    {
        var frame = new CodeGen.ReadHttpFrame(CodeGen.BindingSource.Header, valueType, headerName.Replace("-", "_"))
        {
            Key = headerName
        };
        Middleware.Add(frame);
        variable = frame.Variable;
        return true;
    }

    internal bool FindClaimVariable(Type valueType, string claimType, out Variable variable)
    {
        var frame = new CodeGen.ReadClaimFrame(valueType, claimType);
        Middleware.Add(frame);
        variable = frame.Variable;
        return true;
    }

    private bool tryFindMethodVariable(string methodName, Type returnType, out Variable variable)
    {
        var handlerTypes = HandlerCalls().Select(h => h.HandlerType).Distinct();
        foreach (var type in handlerTypes)
        {
            var method = type
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .FirstOrDefault(m => m.Name.EqualsIgnoreCase(methodName) && m.ReturnType == returnType);

            if (method != null)
            {
                var call = new MethodCall(type, method);
                Middleware.Add(call);
                variable = call.ReturnVariable!;
                return true;
            }
        }

        throw new InvalidOperationException(
            $"Could not find a public static method '{methodName}' returning {returnType.FullNameInCode()} on endpoint types: {handlerTypes.Select(t => t.FullNameInCode()).Join(", ")}");
    }

    private sealed record NormalizedResponseMetadata(int StatusCode, Type? Type, IEnumerable<string> ContentTypes)
    {
        // if an attribute doesn't specific the content type, conform with OpenAPI internals and infer.
        public IEnumerable<string> GetContentTypes()
        {
            if (ContentTypes.Any())
                return ContentTypes;
            if (Type == typeof(string))
                return new[] { "text/plain" };
            if (Type == typeof(ProblemDetails))
                return new[] { "application/problem+json" };
            return new[] { "application/json" };
        }
    }
    private void fillResponseTypes(ApiDescription apiDescription)
    {
        var attributeMetadata = Endpoint!.Metadata
            .OfType<IApiResponseMetadataProvider>()
            .Select(x =>
            {
                var attributeContentTypes = new MediaTypeCollection();
                x.SetContentTypes(attributeContentTypes);
                return new NormalizedResponseMetadata(x.StatusCode, x.Type, attributeContentTypes);
            });

        var responseMetadata = Endpoint.Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(x=> new NormalizedResponseMetadata(x.StatusCode, x.Type, x.ContentTypes));

        // Attributes take priority over computed metadata
        var responseTypes = attributeMetadata.Concat(responseMetadata).GroupBy(x => x.StatusCode);
        
        foreach (var responseTypeMetadata in responseTypes)
        {
            var responseType = responseTypeMetadata.FirstOrDefault(x => x.Type != typeof(void))?.Type;
            var apiResponseType = new ApiResponseType
            {
                StatusCode = responseTypeMetadata.Key,
                ModelMetadata = responseType == null ? null : new EndpointModelMetadata(responseType),
                IsDefaultResponse = false, // this seems to mean "no explicit response", so never set this to true
                ApiResponseFormats = responseTypeMetadata.First().GetContentTypes()
                    .Select(x => new ApiResponseFormat
                    {
                        MediaType = x
                    }).ToList(),
                Type = responseType
            };

            apiDescription.SupportedResponseTypes.Add(apiResponseType);
        }
    }

    private void fillRequestType(ApiDescription apiDescription)
    {
        if (HasRequestType && !IsFormData && apiDescription.HttpMethod != "GET")
        {
            var parameterDescription = new ApiParameterDescription
            {
                Name = RequestType.NameInCode(),
                ModelMetadata = new EndpointModelMetadata(RequestType),
                Source = BindingSource.Body,
                Type = RequestType,
                // A nullable [FromBody] member inside an [AsParameters] type is an optional body. See GH-3135.
                IsRequired = !RequestBodyIsOptional
            };

            apiDescription.ParameterDescriptions.Add(parameterDescription);

            copyAcceptsMetadataToRequestFormats(apiDescription);
        }
    }

    private void fillKnownHeaderParameters(ApiDescription apiDescription)
    {
        foreach (var headerGroup in _headerVariables.GroupBy(x => x.Name))
        {
            var variableType = headerGroup.First().VariableType;
            var parameterDescription = new ApiParameterDescription
            {
                Name = headerGroup.Key,
                ModelMetadata = new EndpointModelMetadata(variableType),
                Source = BindingSource.Header,
                Type = variableType,
                IsRequired = false
            };

            apiDescription.ParameterDescriptions.Add(parameterDescription);
        }
    }

    private void fillQuerystringParameters(ApiDescription apiDescription)
    {
        foreach (var querystringVariable in _querystringVariables)
        {
            var parameterDescription = new ApiParameterDescription
            {
                Name = querystringVariable.Name,
                ModelMetadata = new EndpointModelMetadata(querystringVariable.VariableType),
                Source = BindingSource.Query,
                Type = querystringVariable.VariableType,
                IsRequired = false
            };

            apiDescription.ParameterDescriptions.Add(parameterDescription);
        }
    }

    private void fillFormParameters(ApiDescription apiDescription)
    {
        foreach (var formVariable in _formValueVariables)
        {
            var parameterDescription = new ApiParameterDescription
            {
                Name = formVariable.Name,
                ModelMetadata = new EndpointModelMetadata(formVariable.VariableType),
                Source = BindingSource.Form,
                ParameterDescriptor = new ParameterDescriptor
                {
                    Name = formVariable.Name,
                    ParameterType = formVariable.VariableType
                },
                Type = formVariable.VariableType,
                IsRequired = false
            };

            apiDescription.ParameterDescriptions.Add(parameterDescription);
        }
    }

    /// <summary>
    /// Every method that participates in this endpoint's binding chain: the endpoint method itself plus
    /// every middleware / postprocessor <see cref="MethodCall"/> — compound handler Load/LoadAsync/Before
    /// methods, After/Finally postprocessors, and middleware applied by attributes or policies. The
    /// OpenAPI description is derived from all of them, not just the endpoint method signature. See GH-3380.
    /// </summary>
    private IEnumerable<MethodCall> allMethodCalls()
    {
        yield return Method;

        foreach (var call in Middleware.OfType<MethodCall>())
        {
            yield return call;
        }

        foreach (var call in Postprocessors.OfType<MethodCall>())
        {
            yield return call;
        }
    }

    private ApiParameterDescription buildParameterDescription(RoutePatternParameterPart routeParameter)
    {
        // Match the bound route variable case-insensitively: route tokens are conventionally
        // lower-cased (e.g. {journeyId}) while the bound member/argument is PascalCased
        // (JourneyId). A case-sensitive match here silently misses and falls back to string,
        // losing the real type (e.g. Guid/int) on the generated OpenAPI parameter. See GH-3135.
        var variable = _routeVariables.OfType<CodeGen.HttpElementVariable>()
            .FirstOrDefault(x => x.Name.EqualsIgnoreCase(routeParameter.Name));

        // Route values may also be bound *only* by another method in the chain (a compound handler's
        // LoadAsync, an After postprocessor, middleware applied by a policy). Those frames may not have
        // been resolved yet when the API description is assembled — ASP.NET Core caches the first
        // ApiExplorer read, which can happen long before codegen — so read the binding straight off the
        // method signatures rather than relying on a resolved variable. See GH-3380.
        //
        // When nothing in the chain binds the route value (e.g. a plain complex-body endpoint whose body
        // property overlaps a route token), fall back to the route constraint (`{id:guid}`, `{n:int}`, ...)
        // so the parameter still gets its real type/format.
        //
        // Failing that, honor any type declared through IRoutedChain by middleware that binds the route
        // value through its own frames — the Marten/Polecat aggregate handler workflow declaring the
        // aggregate's identity type for an unconstrained {id}, which is domain knowledge Wolverine.Http
        // cannot infer from the method signatures. Then, finally, string. See GH-3380 and GH-3420.
        // Both OpenAPI stacks schematize from .Type.
        var parameterType = variable?.VariableType
                            ?? typeFromBindingChain(routeParameter.Name)
                            ?? TypeFromRouteConstraint(routeParameter)
                            ?? declaredRouteParameterType(routeParameter.Name)
                            ?? typeof(string);

        var parameter = new ApiParameterDescription
        {
            Name = routeParameter.Name,
            ModelMetadata = new EndpointModelMetadata(parameterType),
            Source = BindingSource.Path,
            //DefaultValue = parameter.DefaultValue,
            Type = parameterType,
            IsRequired = true,
            ParameterDescriptor = new ParameterDescriptor
            {
                Name = routeParameter.Name,
                ParameterType = parameterType
            }
        };
        return parameter;
    }

    /// <summary>
    /// Walks every method in the binding chain looking for an argument — or an [AsParameters] container
    /// member — bound to the named route value, and returns the CLR type it binds to. See GH-3380.
    /// </summary>
    private Type? typeFromBindingChain(string routeName)
    {
        foreach (var call in allMethodCalls())
        {
            foreach (var parameter in call.Method.GetParameters())
            {
                if (parameter.HasAttribute<AsParametersAttribute>())
                {
                    var memberType = routeTypeFromAsParametersMembers(parameter.ParameterType, routeName);
                    if (memberType != null)
                    {
                        return memberType;
                    }

                    continue;
                }

                if (tryDetermineRouteBinding(parameter, routeName, out var parameterType))
                {
                    return parameterType;
                }
            }
        }

        return null;
    }

    private static Type? routeTypeFromAsParametersMembers(Type containerType, string routeName)
    {
        foreach (var property in containerType.GetProperties().Where(x => x.CanRead))
        {
            if (matchesRouteName(property, property.Name, routeName) && isBindableRouteType(property.PropertyType))
            {
                return unwrapNullable(property.PropertyType);
            }
        }

        foreach (var field in containerType.GetFields())
        {
            if (matchesRouteName(field, field.Name, routeName) && isBindableRouteType(field.FieldType))
            {
                return unwrapNullable(field.FieldType);
            }
        }

        return null;
    }

    private static bool tryDetermineRouteBinding(ParameterInfo parameter, string routeName, out Type? parameterType)
    {
        parameterType = null;

        if (!matchesRouteName(parameter, parameter.Name, routeName))
        {
            return false;
        }

        // Only a type that Wolverine could actually bind from a route value counts. This guards against
        // a coincidental name collision with a service or HttpContext element argument.
        if (!isBindableRouteType(parameter.ParameterType))
        {
            return false;
        }

        parameterType = unwrapNullable(parameter.ParameterType);
        return true;
    }

    // An explicit [FromRoute(Name = "order-id")] wins over the member name, matching the runtime binding.
    private static bool matchesRouteName(ICustomAttributeProvider provider, string? memberName, string routeName)
    {
        var attribute = provider.GetCustomAttributes(typeof(FromRouteAttribute), true)
            .OfType<FromRouteAttribute>().FirstOrDefault();

        var boundName = attribute?.Name.IsNotEmpty() == true ? attribute.Name : memberName;

        return boundName != null && boundName.EqualsIgnoreCase(routeName);
    }

    private static bool isBindableRouteType(Type type)
    {
        var inner = unwrapNullable(type);
        return inner == typeof(string) || CodeGen.RouteParameterStrategy.CanParse(inner);
    }

    private static Type unwrapNullable(Type type)
    {
        return type.IsNullable() ? type.GetInnerTypeFromNullable() : type;
    }

    /// <summary>
    /// Adds query string and header parameters that are bound *only* by another method in the binding
    /// chain — a compound handler's Load/LoadAsync/Before, an After/Finally postprocessor, or a middleware
    /// method applied by a policy — and therefore never show up in the endpoint method's own variables.
    /// See GH-3380.
    /// </summary>
    private void fillChainBoundParameters(ApiDescription apiDescription)
    {
        foreach (var call in allMethodCalls())
        {
            foreach (var parameter in call.Method.GetParameters())
            {
                // A simple [FromQuery]/[FromHeader] parameter whose name matches a route-template segment is
                // actually bound from the route value, not the query string or a header: FromQueryAttributeUsage
                // declines simple types and RouteParameterStrategy runs before the query/header strategies, so
                // the route claims it and the attribute is a no-op. The route-template loop already describes it
                // as a Path parameter; describing it again here would emit a second same-name parameter, which is
                // invalid OpenAPI and crashes downstream transformers (e.g. the XML-doc operation transformer's
                // Parameters.SingleOrDefault). Regressed when GH-3380 began deriving parameters from the chain.
                if (isBoundFromRouteValue(parameter))
                {
                    continue;
                }

                if (parameter.TryGetAttribute<FromQueryAttribute>(out var fromQuery))
                {
                    // A complex [FromQuery] type is bound by flattening it into one query string value per
                    // member, all of them already declared by fillQuerystringParameters. The container
                    // itself never appears on the wire, so describing it would add a phantom query
                    // parameter — and drag the type into components/schemas. See GH-3575.
                    if (CodeGen.FromQueryAttributeUsage.IsComplexQueryStringType(parameter.ParameterType))
                    {
                        continue;
                    }

                    var name = fromQuery.Name.IsNotEmpty() ? fromQuery.Name! : parameter.Name!;
                    addChainBoundParameter(apiDescription, name, parameter.ParameterType, BindingSource.Query);
                }
                else if (parameter.TryGetAttribute<FromHeaderAttribute>(out var fromHeader))
                {
                    var name = fromHeader.Name.IsNotEmpty() ? fromHeader.Name! : parameter.Name!;
                    addChainBoundParameter(apiDescription, name, parameter.ParameterType, BindingSource.Header);
                }
            }
        }
    }

    /// <summary>
    /// Would this endpoint/chain parameter be bound from a route value rather than the query string or a
    /// header? RouteParameterStrategy matches by the parameter's own name (case-sensitively, mirroring
    /// <see cref="FindRouteVariable(ParameterInfo, out Variable?)"/>) and runs before the query/header
    /// strategies, so a name collision with a route-template segment on a route-bindable type makes any
    /// [FromQuery]/[FromHeader] attribute a no-op. Such a parameter is already described by the
    /// route-template loop and must not be described again as chain-bound. See GH-3380.
    /// </summary>
    private bool isBoundFromRouteValue(ParameterInfo parameter)
    {
        // An explicit [FromRoute] parameter is route-bound by definition and never treated as chain-bound
        // query/header anyway; only the implicit name-collision case needs guarding here.
        //
        // The name comparison is deliberately CASE-SENSITIVE and must stay that way: it mirrors
        // FindRouteVariable(ParameterInfo) (HttpChain.cs, `x.Name == parameter.Name`), which is the binder
        // path that actually claims the parameter. This is intentionally stricter than matchesRouteName /
        // tryDetermineRouteBinding above (which use EqualsIgnoreCase). Do NOT unify this with those helpers:
        // going case-insensitive here would suppress a query/header parameter the generated code really does
        // read (e.g. `[WolverineGet("/things/{Id}")] Get([FromQuery] string id)` binds `id` from the query
        // string, not the route, because the route segment `Id` never matches the parameter `id`). If the
        // binder's route-name matching is ever aligned to ASP.NET's OrdinalIgnoreCase, this must move with
        // it. See GH-3586.
        return RoutePattern!.Parameters.Any(x => x.Name == parameter.Name)
               && isBindableRouteType(parameter.ParameterType);
    }

    private static void addChainBoundParameter(ApiDescription apiDescription, string name, Type parameterType,
        BindingSource source)
    {
        if (apiDescription.ParameterDescriptions.Any(x => x.Source == source && x.Name.EqualsIgnoreCase(name)))
        {
            return;
        }

        apiDescription.ParameterDescriptions.Add(new ApiParameterDescription
        {
            Name = name,
            ModelMetadata = new EndpointModelMetadata(parameterType),
            Source = source,
            Type = parameterType,
            IsRequired = false
        });
    }

    // Maps an inline route constraint to the CLR type ASP.NET's own route binding would use, so the
    // generated OpenAPI parameter carries the right schema type/format (e.g. {id:guid} -> uuid,
    // {n:int} -> integer). Returns null for `string`/unconstrained or constraints that don't imply a
    // type (length/regex/min/max/etc.), letting the caller default to string. See GH-3135.
    private static Type? TypeFromRouteConstraint(RoutePatternParameterPart routeParameter)
    {
        foreach (var policy in routeParameter.ParameterPolicies)
        {
            var content = policy.Content;
            if (content.IsEmpty())
            {
                continue;
            }

            // Constraints can be parameterized (e.g. "length(5)", "regex(...)"); only the leading
            // constraint name carries the type signal.
            var parenIndex = content.IndexOf('(');
            var name = parenIndex > 0 ? content[..parenIndex] : content;

            switch (name.ToLowerInvariant())
            {
                case "int":
                    return typeof(int);
                case "long":
                    return typeof(long);
                case "bool":
                    return typeof(bool);
                case "datetime":
                    return typeof(DateTime);
                case "decimal":
                    return typeof(decimal);
                case "double":
                    return typeof(double);
                case "float":
                    return typeof(float);
                case "guid":
                    return typeof(Guid);
            }
        }

        return null;
    }
}

internal class EndpointModelMetadata : ModelMetadata
{
    public EndpointModelMetadata(Type modelType) : base(ModelMetadataIdentity.ForType(modelType))
    {
        IsBindingAllowed = true;
    }

    public override IReadOnlyDictionary<object, object> AdditionalValues => ImmutableDictionary<object, object>.Empty;

    public override string? BinderModelName { get; }
    public override Type? BinderType { get; }
    public override BindingSource? BindingSource { get; }
    public override bool ConvertEmptyStringToNull { get; }
    public override string? DataTypeName { get; }
    public override string? Description { get; }
    public override string? DisplayFormatString { get; }
    public override string? DisplayName { get; }
    public override string? EditFormatString { get; }
    public override ModelMetadata? ElementMetadata { get; }
    public override IEnumerable<KeyValuePair<EnumGroupAndName, string>>? EnumGroupedDisplayNamesAndValues { get; }
    public override IReadOnlyDictionary<string, string>? EnumNamesAndValues { get; }
    public override bool HasNonDefaultEditFormat { get; }
    public override bool HideSurroundingHtml { get; }
    public override bool HtmlEncode { get; }
    public override bool IsBindingAllowed { get; }
    public override bool IsBindingRequired { get; }
    public override bool IsEnum { get; }
    public override bool IsFlagsEnum { get; }
    public override bool IsReadOnly { get; }
    public override bool IsRequired { get; }

    public override ModelBindingMessageProvider ModelBindingMessageProvider { get; } =
        new DefaultModelBindingMessageProvider();

    public override string? NullDisplayText { get; }
    public override int Order { get; }
    public override string? Placeholder { get; }
    public override ModelPropertyCollection Properties { get; } = new([]);
    public override IPropertyFilterProvider? PropertyFilterProvider { get; }
    public override Func<object, object>? PropertyGetter { get; }
    public override Action<object, object?>? PropertySetter { get; }
    public override bool ShowForDisplay { get; }
    public override bool ShowForEdit { get; }
    public override string? SimpleDisplayProperty { get; }
    public override string? TemplateHint { get; }
    public override bool ValidateChildren { get; }
    public override IReadOnlyList<object> ValidatorMetadata { get; } = Array.Empty<object>();
}
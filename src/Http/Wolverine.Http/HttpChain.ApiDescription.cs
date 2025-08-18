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
            EndpointMetadata = chain.Endpoint!.Metadata.ToArray();
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
            GroupName = Endpoint.Metadata.GetMetadata<IEndpointGroupNameMetadata>()?.EndpointGroupName,
            RelativePath = Endpoint.RoutePattern.RawText?.TrimStart('/'),
            ActionDescriptor = new WolverineActionDescriptor(this)
        };
        
        foreach (var routeParameter in RoutePattern.Parameters)
        {
            var parameter = buildParameterDescription(routeParameter);

            apiDescription.ParameterDescriptions.Add(parameter);
        }

        fillRequestType(apiDescription);

        fillQuerystringParameters(apiDescription);

        fillFormParameters(apiDescription);

        fillKnownHeaderParameters(apiDescription);

        fillResponseTypes(apiDescription);

        foreach (var parameter in FileParameters)
        {
            var parameterDescription = new ApiParameterDescription
            {
                Name = parameter.Name,
                ModelMetadata = new EndpointModelMetadata(parameter.ParameterType),
                Source = BindingSource.FormFile,
                ParameterDescriptor = new ParameterDescriptor
                {
                    Name = parameter.Name,
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
                Name = formMetadata.Name,
                ModelMetadata = new EndpointModelMetadata(typeof(IFormFile)),
                Source = BindingSource.Form,
                ParameterDescriptor = new ParameterDescriptor
                {
                    Name = formMetadata.Name,
                    ParameterType = typeof(IFormFile)
                },
                Type = typeof(IFormFile),
                IsRequired = true
            };

            apiDescription.ParameterDescriptions.Add(parameterDescription);
        }

        return apiDescription;
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
        if ((source == ValueSource.RouteValue || source == ValueSource.Anything) && FindRouteVariable(valueType, valueName, out variable))
        {
            return true;
        }
        
        if ((source == ValueSource.FromQueryString || source == ValueSource.Anything) && FindQuerystringVariable(valueType, valueName, out variable))
        {
            return true;
        }
        
        if (HasRequestType)
        {
            var requestType = InputType();
            var member = requestType.GetProperties()
                             .FirstOrDefault(x => x.Name.EqualsIgnoreCase(valueName) && x.PropertyType == valueType)
                         ?? (MemberInfo)requestType.GetFields()
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
                IsRequired = true
            };

            apiDescription.ParameterDescriptions.Add(parameterDescription);

            foreach (var metadata in Endpoint.Metadata.OfType<IAcceptsMetadata>())
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
                Type = formVariable.VariableType,
                IsRequired = false
            };

            apiDescription.ParameterDescriptions.Add(parameterDescription);
        }
    }

    private ApiParameterDescription buildParameterDescription(RoutePatternParameterPart routeParameter)
    {
        var variable = _routeVariables.FirstOrDefault(x => x.Usage == routeParameter.Name);

        var parameterType = variable?.VariableType ?? typeof(string);
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
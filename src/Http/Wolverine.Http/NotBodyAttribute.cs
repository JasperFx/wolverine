using Microsoft.AspNetCore.Http.Metadata;

namespace Wolverine.Http;

/// <summary>
///     When used on an HTTP endpoint method, this attribute tells
///     Wolverine that this parameter is *not* sourced from the
///     HTTP request body
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue)]
public class NotBodyAttribute : Attribute, IFromServiceMetadata
{
}


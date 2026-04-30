using Asp.Versioning;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Fluent builder for configuring a deprecation policy on a specific API version.
/// Obtain an instance from <see cref="WolverineApiVersioningOptions.Deprecate(ApiVersion)"/>.
/// </summary>
public interface IWolverineDeprecationPolicyBuilder
{
    /// <summary>Set the deprecation date for this version.</summary>
    /// <param name="date">The date on which the version is deprecated.</param>
    /// <returns>The builder for chaining.</returns>
    IWolverineDeprecationPolicyBuilder On(DateTimeOffset date);

    /// <summary>
    /// Add an RFC 8288 Link header reference pointing to information about this deprecation.
    /// </summary>
    /// <param name="uri">The link target URI.</param>
    /// <param name="title">Optional human-readable title for the link.</param>
    /// <param name="type">Optional media type hint for the linked resource.</param>
    /// <returns>The builder for chaining.</returns>
    IWolverineDeprecationPolicyBuilder WithLink(Uri uri, string? title = null, string? type = null);
}

internal sealed class DeprecationPolicyBuilder : IWolverineDeprecationPolicyBuilder
{
    private readonly WolverineApiVersioningOptions _options;
    private readonly ApiVersion _version;
    private DateTimeOffset? _date;
    private readonly List<LinkHeaderValue> _links = new();

    internal DeprecationPolicyBuilder(WolverineApiVersioningOptions options, ApiVersion version)
    {
        _options = options;
        _version = version;
    }

    /// <inheritdoc/>
    public IWolverineDeprecationPolicyBuilder On(DateTimeOffset date)
    {
        _date = date;
        CommitPolicy();
        return this;
    }

    /// <inheritdoc/>
    public IWolverineDeprecationPolicyBuilder WithLink(Uri uri, string? title = null, string? type = null)
    {
        var link = new LinkHeaderValue(uri, "deprecation");
        if (title != null) link.Title = title;
        if (type != null) link.Type = type;
        _links.Add(link);
        CommitPolicy();
        return this;
    }

    private void CommitPolicy()
    {
        DeprecationPolicy policy;

        if (_date.HasValue && _links.Count > 0)
        {
            policy = new DeprecationPolicy(_date.Value, _links[0]);
            for (var i = 1; i < _links.Count; i++)
            {
                policy.Links.Add(_links[i]);
            }
        }
        else if (_date.HasValue)
        {
            policy = new DeprecationPolicy(_date.Value);
        }
        else if (_links.Count > 0)
        {
            policy = new DeprecationPolicy(_links[0]);
            for (var i = 1; i < _links.Count; i++)
            {
                policy.Links.Add(_links[i]);
            }
        }
        else
        {
            policy = new DeprecationPolicy();
        }

        _options.DeprecationPolicies[_version] = policy;
    }
}

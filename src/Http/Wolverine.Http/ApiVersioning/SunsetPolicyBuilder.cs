using Asp.Versioning;

namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Fluent builder for configuring a sunset policy on a specific API version.
/// Obtain an instance from <see cref="WolverineApiVersioningOptions.Sunset(ApiVersion)"/>.
/// </summary>
public interface IWolverineSunsetPolicyBuilder
{
    /// <summary>Set the sunset date for this version.</summary>
    /// <param name="date">The date on which the version will sunset.</param>
    /// <returns>The builder for chaining.</returns>
    IWolverineSunsetPolicyBuilder On(DateTimeOffset date);

    /// <summary>
    /// Add an RFC 8288 Link header reference pointing to information about this sunset.
    /// </summary>
    /// <param name="uri">The link target URI.</param>
    /// <param name="title">Optional human-readable title for the link.</param>
    /// <param name="type">Optional media type hint for the linked resource.</param>
    /// <returns>The builder for chaining.</returns>
    IWolverineSunsetPolicyBuilder WithLink(Uri uri, string? title = null, string? type = null);
}

internal sealed class SunsetPolicyBuilder : IWolverineSunsetPolicyBuilder
{
    private readonly WolverineApiVersioningOptions _options;
    private readonly ApiVersion _version;
    private DateTimeOffset? _date;
    private readonly List<LinkHeaderValue> _links = new();

    internal SunsetPolicyBuilder(WolverineApiVersioningOptions options, ApiVersion version)
    {
        _options = options;
        _version = version;
    }

    /// <inheritdoc/>
    public IWolverineSunsetPolicyBuilder On(DateTimeOffset date)
    {
        _date = date;
        CommitPolicy();
        return this;
    }

    /// <inheritdoc/>
    public IWolverineSunsetPolicyBuilder WithLink(Uri uri, string? title = null, string? type = null)
    {
        var link = new LinkHeaderValue(uri, "sunset");
        if (title != null) link.Title = title;
        if (type != null) link.Type = type;
        _links.Add(link);
        CommitPolicy();
        return this;
    }

    private void CommitPolicy()
    {
        SunsetPolicy policy;

        if (_date.HasValue && _links.Count > 0)
        {
            policy = new SunsetPolicy(_date.Value, _links[0]);
            for (var i = 1; i < _links.Count; i++)
            {
                policy.Links.Add(_links[i]);
            }
        }
        else if (_date.HasValue)
        {
            policy = new SunsetPolicy(_date.Value);
        }
        else if (_links.Count > 0)
        {
            policy = new SunsetPolicy(_links[0]);
            for (var i = 1; i < _links.Count; i++)
            {
                policy.Links.Add(_links[i]);
            }
        }
        else
        {
            policy = new SunsetPolicy();
        }

        _options.SunsetPolicies[_version] = policy;
    }
}

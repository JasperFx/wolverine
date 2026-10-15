using Microsoft.AspNetCore.Builder;

namespace Wolverine.Http;

/// <summary>
///     GH-5037. The authorization decision that mapping one of Wolverine's operational endpoints, such as the
///     durability pass endpoint or a push receiver, requires. These endpoints send messages, handle messages and
///     delete rows, so mapping one fails until you either name the authorization it requires with
///     <see cref="RequireAuthorization" /> or opt out of authorization explicitly with <see cref="AllowAnonymous" />
///     because something in front of the application, such as Cloud Run IAM or a function key, already protects it
/// </summary>
public abstract class ProtectedEndpointOptions
{
    private string[]? _policyNames;
    private bool _allowAnonymous;

    /// <summary>
    ///     Require the named authorization policies, or the default authorization policy when none are named
    /// </summary>
    public void RequireAuthorization(params string[] policyNames)
    {
        _policyNames = policyNames;
        _allowAnonymous = false;
    }

    /// <summary>
    ///     Map the endpoint with no authorization at all. Only do this when something in front of the application
    ///     already decides who can call it
    /// </summary>
    public void AllowAnonymous()
    {
        _allowAnonymous = true;
        _policyNames = null;
    }

    // Checked before anything is mapped, so an undecided endpoint never reaches the route table
    internal void AssertDecided(string endpoint)
    {
        if (!_allowAnonymous && _policyNames == null)
        {
            throw new InvalidOperationException(
                $"Mapping {endpoint} requires an authorization decision. Call RequireAuthorization(\"policy\") on its options, or AllowAnonymous() if something in front of the application already protects it.");
        }
    }

    internal void ApplyTo(IEndpointConventionBuilder builder)
    {
        if (_allowAnonymous)
        {
            builder.AllowAnonymous();
        }
        else
        {
            builder.RequireAuthorization(_policyNames!);
        }
    }
}

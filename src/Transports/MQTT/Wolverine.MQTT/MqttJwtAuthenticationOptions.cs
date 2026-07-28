namespace Wolverine.MQTT;

/// <summary>
/// Configures MQTT v5 enhanced authentication with a bearer token. Wolverine sends the token from
/// <paramref name="GetTokenCallBack"/> as the <c>AuthenticationData</c> of the CONNECT packet, then re-authenticates
/// with a freshly fetched token every <paramref name="RefreshPeriod"/> while the client is connected.
/// </summary>
/// <param name="GetTokenCallBack">Returns the raw token bytes. Use UTF-8 encoding if your token is a string.</param>
/// <param name="RefreshPeriod">How often a new token is fetched and sent to the broker as a re-authentication.</param>
public record MqttJwtAuthenticationOptions(Func<Task<byte[]>> GetTokenCallBack, TimeSpan RefreshPeriod)
{
    /// <summary>
    /// The default MQTT v5 authentication method name for OAuth2 bearer tokens.
    /// </summary>
    public const string OAuth2Jwt = "OAUTH2-JWT";

    /// <summary>
    /// The authentication method name required by Azure Event Grid when authenticating an MQTT client with a
    /// custom JWT. See https://learn.microsoft.com/en-us/azure/event-grid/mqtt-client-custom-jwt
    /// </summary>
    public const string CustomJwt = "CUSTOM-JWT";

    // GH-3588: this is an overload rather than a third defaulted parameter on the primary constructor so that the
    // existing two argument constructor keeps its exact signature and assemblies compiled against earlier
    // Wolverine versions keep binding to it.
    /// <summary>
    /// Configures MQTT v5 enhanced authentication with a bearer token and a broker specific authentication method
    /// name, for brokers that do not use the default <see cref="OAuth2Jwt"/>.
    /// </summary>
    /// <param name="GetTokenCallBack">Returns the raw token bytes. Use UTF-8 encoding if your token is a string.</param>
    /// <param name="RefreshPeriod">How often a new token is fetched and sent to the broker as a re-authentication.</param>
    /// <param name="AuthenticationMethod">
    /// The MQTT v5 authentication method name advertised to the broker. Azure Event Grid's custom JWT
    /// authentication, for example, requires <see cref="CustomJwt"/>.
    /// </param>
    public MqttJwtAuthenticationOptions(Func<Task<byte[]>> GetTokenCallBack, TimeSpan RefreshPeriod,
        string AuthenticationMethod) : this(GetTokenCallBack, RefreshPeriod)
    {
        this.AuthenticationMethod = AuthenticationMethod;
    }

    private readonly string _authenticationMethod = OAuth2Jwt;

    /// <summary>
    /// The MQTT v5 authentication method name advertised to the broker. Defaults to <see cref="OAuth2Jwt"/>.
    /// </summary>
    public string AuthenticationMethod
    {
        get => _authenticationMethod;
        init => _authenticationMethod = validated(value);
    }

    private static string validated(string method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException(
                $"The MQTT authentication method is required. Use {nameof(MqttJwtAuthenticationOptions)}.{nameof(OAuth2Jwt)} unless your broker requires something else.",
                nameof(AuthenticationMethod));
        }

        return method;
    }
}

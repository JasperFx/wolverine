using System.Text;
using JasperFx.Core;
using MQTTnet.Client;
using Shouldly;
using Wolverine.MQTT.Internals;

namespace Wolverine.MQTT.Tests;

// GH-3588: brokers disagree on the *name* of the authentication method used for the very same OAuth2 bearer token
// exchange. Azure Event Grid wants "CUSTOM-JWT" where the default is "OAUTH2-JWT", so the name is configurable.
public class MqttJwtAuthenticationMethodTests
{
    private static Func<Task<byte[]>> TokenIs(string token) => () => Task.FromResult(Encoding.UTF8.GetBytes(token));

    [Fact]
    public void authentication_method_defaults_to_oauth2_jwt()
    {
        var options = new MqttJwtAuthenticationOptions(TokenIs("token"), 30.Minutes());

        options.AuthenticationMethod.ShouldBe("OAUTH2-JWT");
        options.AuthenticationMethod.ShouldBe(MqttJwtAuthenticationOptions.OAuth2Jwt);
    }

    [Fact]
    public void can_override_the_authentication_method()
    {
        var options = new MqttJwtAuthenticationOptions(TokenIs("token"), 30.Minutes(),
            MqttJwtAuthenticationOptions.CustomJwt);

        options.AuthenticationMethod.ShouldBe("CUSTOM-JWT");
    }

    [Fact]
    public void can_use_an_arbitrary_broker_specific_authentication_method()
    {
        var options = new MqttJwtAuthenticationOptions(TokenIs("token"), 30.Minutes(), "K8S-SAT");

        options.AuthenticationMethod.ShouldBe("K8S-SAT");
    }

    [Fact]
    public void the_two_argument_constructor_is_preserved_for_binary_compatibility()
    {
        // GH-3588: the authentication method arrived as a constructor *overload* rather than a third defaulted
        // parameter, so that assemblies compiled against earlier Wolverine versions keep binding to the
        // two argument constructor that already shipped.
        typeof(MqttJwtAuthenticationOptions)
            .GetConstructor([typeof(Func<Task<byte[]>>), typeof(TimeSpan)])
            .ShouldNotBeNull();

        typeof(MqttJwtAuthenticationOptions)
            .GetConstructor([typeof(Func<Task<byte[]>>), typeof(TimeSpan), typeof(string)])
            .ShouldNotBeNull();

        // The positional deconstruction that shipped is unchanged as well
        typeof(MqttJwtAuthenticationOptions).GetMethod("Deconstruct")!
            .GetParameters().Length.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void empty_authentication_method_is_rejected(string? method)
    {
        Should.Throw<ArgumentException>(() =>
            new MqttJwtAuthenticationOptions(TokenIs("token"), 30.Minutes(), method!));
    }

    [Fact]
    public void with_expression_carries_and_validates_the_authentication_method()
    {
        var options = new MqttJwtAuthenticationOptions(TokenIs("token"), 30.Minutes());

        var custom = options with { AuthenticationMethod = MqttJwtAuthenticationOptions.CustomJwt };
        custom.AuthenticationMethod.ShouldBe("CUSTOM-JWT");

        // the original is untouched
        options.AuthenticationMethod.ShouldBe("OAUTH2-JWT");

        Should.Throw<ArgumentException>(() => options with { AuthenticationMethod = "" });
    }

    [Fact]
    public async Task applies_the_default_authentication_method_and_token_to_the_client_options()
    {
        var clientOptions = new MqttClientOptions();
        var jwt = new MqttJwtAuthenticationOptions(TokenIs("first-token"), 30.Minutes());

        await MqttTransport.ApplyJwtAuthenticationAsync(clientOptions, jwt);

        clientOptions.AuthenticationMethod.ShouldBe("OAUTH2-JWT");
        Encoding.UTF8.GetString(clientOptions.AuthenticationData).ShouldBe("first-token");
    }

    [Fact]
    public async Task applies_a_custom_authentication_method_to_the_client_options()
    {
        var clientOptions = new MqttClientOptions();
        var jwt = new MqttJwtAuthenticationOptions(TokenIs("first-token"), 30.Minutes(),
            MqttJwtAuthenticationOptions.CustomJwt);

        await MqttTransport.ApplyJwtAuthenticationAsync(clientOptions, jwt);

        clientOptions.AuthenticationMethod.ShouldBe("CUSTOM-JWT");
        Encoding.UTF8.GetString(clientOptions.AuthenticationData).ShouldBe("first-token");
    }

    [Fact]
    public async Task overwrites_an_authentication_method_that_was_set_directly_on_the_client_options()
    {
        // If you hand-rolled WithAuthentication() *and* passed MqttJwtAuthenticationOptions, the Wolverine
        // options win - they are the ones driving the re-authentication loop.
        var clientOptions = new MqttClientOptions
        {
            AuthenticationMethod = "SOMETHING-ELSE",
            AuthenticationData = Encoding.UTF8.GetBytes("stale-token")
        };

        var jwt = new MqttJwtAuthenticationOptions(TokenIs("fresh-token"), 30.Minutes(),
            MqttJwtAuthenticationOptions.CustomJwt);

        await MqttTransport.ApplyJwtAuthenticationAsync(clientOptions, jwt);

        clientOptions.AuthenticationMethod.ShouldBe("CUSTOM-JWT");
        Encoding.UTF8.GetString(clientOptions.AuthenticationData).ShouldBe("fresh-token");
    }

    [Fact]
    public async Task the_token_callback_is_invoked_on_every_application()
    {
        var count = 0;
        var jwt = new MqttJwtAuthenticationOptions(() => Task.FromResult(Encoding.UTF8.GetBytes($"token-{++count}")),
            30.Minutes(), MqttJwtAuthenticationOptions.CustomJwt);

        var first = new MqttClientOptions();
        var second = new MqttClientOptions();

        await MqttTransport.ApplyJwtAuthenticationAsync(first, jwt);
        await MqttTransport.ApplyJwtAuthenticationAsync(second, jwt);

        // Each connection - the default one and every tenant connection - gets its own freshly fetched token
        Encoding.UTF8.GetString(first.AuthenticationData).ShouldBe("token-1");
        Encoding.UTF8.GetString(second.AuthenticationData).ShouldBe("token-2");
    }
}

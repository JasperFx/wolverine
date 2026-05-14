using System.Diagnostics;
using CoreTests.Bugs;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs
{
    public class Bug_312_multiple_handlers_for_same_message_with_same_name_bug_different_namespaces
    {
        [Fact]
        public async Task disambiguate_the_handler_variable_names()
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                    // The opaque lambda-factory registration form is exactly the
                    // case Wolverine 6.0's NotAllowed default rejects: codegen can't
                    // see through it to inline-construct, so it falls back to service
                    // location. Test's purpose is the handler-name disambiguation,
                    // not the lambda form, so allow the type explicitly.
                    opts.CodeGeneration.AlwaysUseServiceLocationFor<IIdentityService>();
                    opts.Services.AddScoped<IIdentityService>(x => new IdentityService());
                }).StartAsync();

            await host.InvokeMessageAndWaitAsync(new SayStuff("Hi"));
        }
    }

    public interface IIdentityService;
    public class IdentityService : IIdentityService;

    public record SayStuff(string Text);

    public class SayStuffHandler
    {
        public void Handle(SayStuff stuff, IIdentityService service) => Debug.WriteLine(stuff.Text);
    }
}

namespace Different
{
    public class SayStuffHandler
    {
        public void Handle(SayStuff stuff) => Debug.WriteLine(stuff.Text);
    }
}
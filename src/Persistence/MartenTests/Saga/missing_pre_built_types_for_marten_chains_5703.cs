using IntegrationTests;
using JasperFx.CodeGeneration;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime.Handlers;

namespace MartenTests.Saga;

// GH-5703. A chain Marten persists -- here a saga kept as a Marten document -- is a handler chain like any other, so a
// missing pre-built type for it fails the start with the same message: the missing types, then how to fix it
public class missing_pre_built_types_for_marten_chains_5703
{
    [Fact]
    public async Task a_marten_saga_without_pre_built_types()
    {
        var ex = await Should.ThrowAsync<MissingPreBuiltTypesException>(() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<RoomBooking>();

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "missing_pre_built_5703";
                }).IntegrateWithWolverine();
            }).StartAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(nameof(BookRoom));
        ex.Message.ShouldContain(nameof(RoomBooking));
        ex.Message.ShouldEndWith(MissingPreBuiltTypesException.HowToFix);
    }
}

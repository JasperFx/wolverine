using JasperFx.Core;
using Microsoft.AspNetCore.Http.Metadata;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class using_create_response_and_metadata_derived_from_response_type : IntegrationContext
{
    public using_create_response_and_metadata_derived_from_response_type(AppFixture fixture) : base(fixture)
    {
    }
    

    [Fact]
    public void read_metadata_from_IEndpointMetadataProvider()
    {
        var chain = HttpChain.ChainFor<CreateEndpoint>(x => x.Create(null));

        var endpoint = chain.BuildEndpoint();
        
        // Should remove the 200 OK response
        endpoint
            .Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Any(x => x.StatusCode == 200)
            .ShouldBeFalse();

        var responseMetadata = endpoint
            .Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .FirstOrDefault(x => x.StatusCode == 201);

        responseMetadata.ShouldNotBeNull();
        responseMetadata.Type.ShouldBe(typeof(IssueCreated));
    }

    [Fact]
    public async Task make_the_request()
    {
        await Store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(Issue));

        var result = await Scenario(x =>
        {
            x.Post.Json(new CreateIssue("It's bad")).ToUrl("/issue");
            x.StatusCodeShouldBe(201);

        });

        var created = result.ReadAsJson<IssueCreated>();
        created.ShouldNotBeNull();

        using var session = Store.LightweightSession();
        var issue = await session.LoadAsync<Issue>(created.Id);
        issue.ShouldNotBeNull();
        issue.Title.ShouldBe("It's bad");
        
        result.Context.Response.Headers.Location.Single().ShouldBe("/issue/" + created.Id);
    }
}
#region sample_aspire_localstack_apphost
var builder = DistributedApplication.CreateBuilder(args);

// LocalStack running SQS and SNS in a container. The name "aws" becomes the connection string
// injected into every project that references it, ConnectionStrings__aws, and its value is the
// emulator's endpoint, http://{host}:{port}
var aws = builder.AddLocalStack("aws");

builder.AddProject<Projects.AspireWithLocalStack_Pinger>("pinger")
    // Also sets AWS_ENDPOINT_URL, AWS_REGION and placeholder credentials, so any AWS SDK client
    // the project builds itself talks to LocalStack as well
    .WithReference(aws)
    // The pinger does not start until LocalStack's health endpoint answers
    .WaitFor(aws)
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
#endregion

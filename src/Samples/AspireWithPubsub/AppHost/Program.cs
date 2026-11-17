#region sample_aspire_pubsub_apphost
var builder = DistributedApplication.CreateBuilder(args);

// The gcloud Pub/Sub emulator in a container, started for the "wolverine" project. The name
// "pubsub" becomes the connection string injected into every project that references it,
// ConnectionStrings__pubsub, and its value is the emulator's endpoint, {host}:{port}
var pubsub = builder.AddPubsubEmulator("pubsub", projectId: "wolverine");

builder.AddProject<Projects.AspireWithPubsub_Pinger>("pinger")
    // Also sets PUBSUB_EMULATOR_HOST and PUBSUB_PROJECT_ID for any Google client the
    // project builds itself
    .WithReference(pubsub)
    // The pinger does not start until the emulator answers
    .WaitFor(pubsub)
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
#endregion

#region sample_grpc_pinger_bootstrapping

using GrpcPinger;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedService<PingWorker>();

var host = builder.Build();
await host.RunAsync();

#endregion

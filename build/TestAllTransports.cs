using System;
using Nuke.Common;

partial class Build
{
    Target TestAllTransports => _ => _
        .DependsOn(Compile, DockerUp)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            // DockerUp has the compose Pulsar running, so point the Pulsar fixture at it exactly as
            // CIPulsar and LocalGate do. Unset, every worker process starts its own Testcontainers
            // broker (GH-3799), and on a loaded machine the supervisor killed the first as a worker
            // that never became usable before Pulsar's standalone image finished booting.
            Environment.SetEnvironmentVariable("WOLVERINE_PULSAR", "pulsar://localhost:6650");
            Environment.SetEnvironmentVariable("WOLVERINE_PULSAR_HTTP", "http://localhost:8080");

            var transportsDir = RootDirectory / "src" / "Transports";
            RunAllTestsProjects(transportsDir);
        });
}

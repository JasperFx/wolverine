using Nuke.Common;

partial class Build
{
    Target TestAllTransports => _ => _
        .DependsOn(Compile, DockerUp)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var transportsDir = RootDirectory / "src" / "Transports";
            RunTestProjectsOneClassAtATime(transportsDir);
        });
}

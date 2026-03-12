using System;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    Target TestAllTransports => _ => _
        .DependsOn(Compile, DockerUp)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var transportsDir = RootDirectory / "src" / "Transports";
            RunTestProjectsOneFileAtATime(transportsDir);
        });
}

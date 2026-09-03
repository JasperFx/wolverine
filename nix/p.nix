# The P model checker (https://p-org.github.io/P), which upstream ships only as the
# `P` NuGet tool package. `nix run .#p`, or just `p` inside the dev shell.
#
# P targets net8.0 and generates net8.0 projects, so whatever DOTNET_ROOT it runs
# under has to carry the .NET 8 SDK. `buildDotnetGlobalTool` sets useDotnetFromEnv,
# so the dev shell's combined SDK wins; dotnet-runtime here is only the fallback for
# running `p` outside the shell.
{
  lib,
  buildDotnetGlobalTool,
  dotnetCorePackages,
}:

buildDotnetGlobalTool {
  pname = "p";
  nugetName = "P";
  version = "3.1.0";
  nugetHash = "sha256-sqIS47GvG/L9ybgImdopAdZiXRouR41HjjACiHKkvcE=";

  dotnet-runtime = dotnetCorePackages.sdk_8_0;

  meta = {
    description = "P programming language compiler and model checker";
    homepage = "https://p-org.github.io/P/";
    license = lib.licenses.mit;
    mainProgram = "p";
  };
}

{
  description = "Wolverine — .NET message bus / mediator, plus the P model checker for formal/";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";
  };

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f {
        inherit system;
        pkgs = import nixpkgs { inherit system; };
      });

      # One DOTNET_ROOT holding every SDK this repo needs. Wolverine itself builds for
      # net9.0/net10.0 (CI pins net9.0 — see CLAUDE.md); the P model checker is a net8.0
      # tool that builds and runs net8.0 projects of its own, and an SDK can't run a
      # framework it doesn't ship. The first entry provides the CLI, so `dotnet` is 10.
      dotnetFor = pkgs: pkgs.dotnetCorePackages.combinePackages [
        pkgs.dotnetCorePackages.sdk_10_0
        pkgs.dotnetCorePackages.sdk_9_0
        pkgs.dotnetCorePackages.sdk_8_0
      ];
    in
    {
      packages = forAllSystems ({ pkgs, system }: {
        # The P model checker (https://p-org.github.io/P), used by the specs under
        # formal/. The dev shell takes it from here; on its own it wants a .NET 8
        # root, so prefer the shell's `p` over `nix run`.
        p = pkgs.callPackage ./nix/p.nix { };
      });

      devShells = forAllSystems ({ pkgs, system }: {
        default = pkgs.mkShell {
          packages = [
            (dotnetFor pkgs)
            # Formal specs under formal/: the P model checker. Its default bugfinding
            # mode is pure .NET; the PEx and PObserve backends generate Java and build
            # it with Maven, which is what the JDK and maven here are for.
            self.packages.${system}.p
            pkgs.jdk_headless
            pkgs.maven
          ];

          # Keep the SDK from phoning home / printing the first-run banner.
          env = {
            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            # Point the CLI at the Nix-provided SDK explicitly.
            DOTNET_ROOT = "${dotnetFor pkgs}/share/dotnet";
          };

          shellHook = ''
            echo "wolverine dev shell"
            echo "  .NET  $(dotnet --version)"
            echo "  P     $(p --version 2>/dev/null | head -1)"
          '';
        };
      });
    };
}

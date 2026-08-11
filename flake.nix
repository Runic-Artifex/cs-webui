{
  description = "Modern .NET bindings for WebUI";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    webui = {
      # Runic fork revision carrying the server destroy lifecycle fix under test.
      url = "github:Runic-Artifex/webui/a800dde44db1bffcea8d18ccdad5c2e5950940fa";
      flake = false;
    };
  };

  outputs = { self, nixpkgs, webui }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
      ];

      forAllSystems = f:
        nixpkgs.lib.genAttrs systems (system:
          f (import nixpkgs {
            inherit system;
            config.allowUnfree = true;
          }));
    in
    {
      packages = forAllSystems (pkgs:
        let
          webuiNative = pkgs.stdenv.mkDerivation {
            pname = "webui-2";
            version = "2.5.0-beta.4";
            src = webui;

            nativeBuildInputs = [
              pkgs.cmake
              pkgs.ninja
            ];

            cmakeFlags = [
              "-DBUILD_SHARED_LIBS=ON"
              "-DWEBUI_BUILD_EXAMPLES=OFF"
              "-DBUILD_TESTING=OFF"
              "-DWEBUI_OUT_LIB_NAME=webui-2"
              "-DWEBUI_USE_TLS=OFF"
            ];

            meta = with pkgs.lib; {
              description = "WebUI native shared library used by CS-WebUI";
              homepage = "https://webui.me/";
              license = licenses.mit;
              platforms = platforms.unix;
            };
          };
        in
        {
          webui-native = webuiNative;
          default = webuiNative;
        });

      checks = forAllSystems (pkgs: {
        webui-native = self.packages.${pkgs.stdenv.hostPlatform.system}.webui-native;
        abi = pkgs.runCommand "cswebui-abi" {
          nativeBuildInputs = [
            pkgs.bash
            pkgs.coreutils
            pkgs.diffutils
            pkgs.gnugrep
            pkgs.gnused
          ];
        } ''
          ${pkgs.bash}/bin/bash ${self}/eng/validate-webui-abi.sh ${webui}/include/webui.h
          touch "$out"
        '';
      });

      devShells = forAllSystems (pkgs:
        let
          inherit (pkgs) lib;
          webuiNative = self.packages.${pkgs.stdenv.hostPlatform.system}.webui-native;
          isLinux = pkgs.stdenv.hostPlatform.isLinux;
          nativeLibraryName = if pkgs.stdenv.hostPlatform.isDarwin
            then "libwebui-2.dylib"
            else "libwebui-2.so";
          linuxRuntimePackages = with pkgs; lib.optionals isLinux [
            chromium
            gtk3
            webkitgtk_4_1
            xvfb-run
          ];
          linuxLibraryPath = lib.makeLibraryPath linuxRuntimePackages;
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              clang
              cmake
              curl
              dotnet-sdk_10
              git
              jq
              ninja
              pkg-config
              unzip
            ] ++ linuxRuntimePackages;

            shellHook = ''
              export DOTNET_CLI_TELEMETRY_OPTOUT=1
              export DOTNET_NOLOGO=1
              export NUGET_PACKAGES="$PWD/.nuget/packages"
              export CSWEBUI_NATIVE_LIBRARY="${webuiNative}/lib/${nativeLibraryName}"
              ${lib.optionalString isLinux ''
                export LD_LIBRARY_PATH="${linuxLibraryPath}:$LD_LIBRARY_PATH"
                export WEBUI_BROWSER_PATH="${pkgs.chromium}/bin/chromium"
              ''}
            '';
          };
        });
    };
}

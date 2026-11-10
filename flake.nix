{
  description = "Modern .NET bindings for WebUI";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    webui = {
      # Match the exact official upstream revision selected by the maintained
      # nightly-asset authority.
      url = "github:webui-dev/webui/24c7d196edc63f5b777d465063c3d26731f52dea";
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
          # global.json is the SDK floor for every CI leg; fail evaluation if the locked nixpkgs SDK is older
          # (10.0.403 / runtime 10.0.14 fixes CVE-2026-111497 in the NativeAOT ILCompiler pack).
          sdkFloor = (lib.importJSON ./global.json).sdk.version;
          dotnetSdk =
            assert lib.assertMsg (lib.versionAtLeast pkgs.dotnet-sdk_10.version sdkFloor)
              "nixpkgs provides .NET SDK ${pkgs.dotnet-sdk_10.version}, but global.json requires ${sdkFloor}";
            pkgs.dotnet-sdk_10;
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              clang
              cmake
              curl
              dotnetSdk
              git
              jq
              ninja
              nodejs_24
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
                # Read by CS-WebUI's WebUiWindow.Show; WebUI itself reads no browser variable.
                export CSWEBUI_BROWSER_PATH="${pkgs.chromium}/bin/chromium"
              ''}
            '';
          };
        });
    };
}

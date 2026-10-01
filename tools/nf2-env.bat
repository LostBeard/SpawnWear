@echo off
REM ============================================================================
REM nanoFramework 2.0 (generics) build/deploy environment for the spawnwear/nf2-generics branch.
REM
REM 2.0 assemblies (NFMRK2) need the PREVIEW nanoFramework tooling. It is NOT installed into Visual Studio
REM (pre- and post-generics extensions cannot share one VS major version); its MSBuild payload and debugger
REM DLL are unpacked into ..\..\nf2-tooling and selected per build:
REM   msbuild <x>.nfproj -p:NanoFrameworkProjectSystemPath=%NF2_PROJECT_SYSTEM%
REM   dotnet run tools\nf-deploy.cs ...      (reads NF_DEBUG_LIBRARY)
REM
REM Usage (cmd):  call tools\nf2-env.bat
REM ============================================================================
set "NF2_TOOLING=%~dp0..\..\nf2-tooling"
set "NF2_VSIX_URL=https://www.vsixgallery.com/extensions/bf694e17-fa5f-4877-9317-6d3664b2689a/.NET%%20nanoFramework%%20Extension%%20v2022.14.2.34.vsix"
if not exist "%NF2_TOOLING%\msbuild\v1.0\NFProjectSystem.MDP.targets" (
    echo ==^> Unpacking the preview nanoFramework extension 2022.14.2.34 into %NF2_TOOLING%
    if not exist "%NF2_TOOLING%" mkdir "%NF2_TOOLING%"
    curl -sL -o "%NF2_TOOLING%\nf-ext.vsix" "%NF2_VSIX_URL%" || exit /b 1
    powershell -NoProfile -Command "Expand-Archive -Force '%NF2_TOOLING%\nf-ext.vsix' '%NF2_TOOLING%\vsix'; New-Item -ItemType Directory -Force '%NF2_TOOLING%\msbuild' | Out-Null; Copy-Item -Recurse -Force '%NF2_TOOLING%\vsix\$MSBuild\nanoFramework\v1.0' '%NF2_TOOLING%\msbuild\'" || exit /b 1
)
set "NF2_PROJECT_SYSTEM=%NF2_TOOLING%\msbuild\v1.0\"
set "NF_DEBUG_LIBRARY=%NF2_TOOLING%\vsix\nanoFramework.Tools.DebugLibrary.Net.dll"
echo NF2_PROJECT_SYSTEM=%NF2_PROJECT_SYSTEM%
echo NF_DEBUG_LIBRARY=%NF_DEBUG_LIBRARY%

# Build and Test

## Requirements

- Visual Studio Build Tools with MSBuild, MSVC x64 C++ toolset, .NET Framework 4.7.2 targeting pack, and .NET Framework 4.8 SDK (`metahost.h`).

## Build Release x64

```powershell
.\Build-Release.ps1
```

Optional parameters:
- `-Version <x.y.z>` (default: `0.9.1`; must match `AssemblyVersion`, `AssemblyFileVersion`, and `app.manifest`)
- `-SkipTests` (skip running unit tests before packaging)

Expected outputs (for version `0.9.1`):

```text
artifacts\HDT-HS-Reconnector\Release-0.9.1\HDT-HS-Reconnector.exe
artifacts\HDT-HS-Reconnector\Release-0.9.1\HdtBridge.dll
artifacts\HDT-HS-Reconnector\Release-0.9.1\HdtBootstrap.dll
artifacts\0.9.1\HDT-HS-Reconnector.exe
artifacts\0.9.1\HdtBridge.dll
artifacts\0.9.1\HdtBootstrap.dll
artifacts\0.9.1\HDT-HS-Reconnector-0.9.1-x64.zip
```

## Run Tests and Verify Binaries Manually

```powershell
.\tests\Run-Tests.ps1
.\tests\Verify-Release.ps1 -Binary 'artifacts\0.9.1\HDT-HS-Reconnector.exe'
.\tests\Verify-Release.ps1 -Binary 'artifacts\0.9.1\HdtBridge.dll'
.\tests\Verify-Release.ps1 -Binary 'artifacts\0.9.1\HdtBootstrap.dll'
```

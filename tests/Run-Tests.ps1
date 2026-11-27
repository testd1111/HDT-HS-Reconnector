param([string]$Compiler)
$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    $Compiler = $env:ROSLYN_CSC
}
if (-not $Compiler) {
    $cmd = Get-Command 'csc.exe' -ErrorAction SilentlyContinue
    if ($cmd) {
        $roslynCandidate = Join-Path (Split-Path $cmd.Source) 'Roslyn\csc.exe'
        if (Test-Path $roslynCandidate) {
            $Compiler = $roslynCandidate
        }
    }
}
if (-not $Compiler) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $vsPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
        if ($vsPath) {
            $candidate = Join-Path $vsPath 'MSBuild\Current\Bin\Roslyn\csc.exe'
            if (Test-Path $candidate) {
                $Compiler = $candidate
            }
        }
    }
}
if (-not $Compiler) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'),
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe'),
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe')
    )
    $Compiler = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Compiler -or -not (Test-Path $Compiler)) {
    throw 'Roslyn csc.exe not found. Pass -Compiler <path-to-Roslyn-csc.exe> or set ROSLYN_CSC.'
}
$repo = Split-Path $PSScriptRoot
$output = Join-Path $repo 'artifacts\tests'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2'
if (-not (Test-Path (Join-Path $framework 'mscorlib.dll'))) {
    throw ".NET Framework 4.7.2 Targeting Pack was not found at '$framework'. Install Microsoft.Net.Component.4.7.2.TargetingPack via Visual Studio Installer or the .NET Framework 4.7.2 Developer Pack."
}
$refs = @('PresentationCore', 'PresentationFramework', 'WindowsBase', 'System.Xaml', 'System.Xml', 'System.Net.Http', 'System.Runtime.Serialization') | ForEach-Object { '/r:' + (Join-Path $framework ($_ + '.dll')) }
$sources = @(
    'HDT-HS-Reconnector\Core\AppLogger.cs',
    'HDT-HS-Reconnector\Core\AppSettings.cs',
    'HDT-HS-Reconnector\Core\HdtBridgeClient.cs',
    'HDT-HS-Reconnector\Core\HdtLocator.cs',
    'HDT-HS-Reconnector\Core\HearthstoneLocator.cs',
    'HDT-HS-Reconnector\Core\HotkeyManager.cs',
    'HDT-HS-Reconnector\Core\Localization.cs',
    'HDT-HS-Reconnector\Core\NetworkProbe.cs',
    'HDT-HS-Reconnector\Core\ReconnectController.cs',
    'HDT-HS-Reconnector\Core\Reconnector.cs',
    'HDT-HS-Reconnector\Core\VersionChecker.cs',
    'HDT-HS-Reconnector\Native\Iphlpapi.cs',
    'HDT-HS-Reconnector\Native\Win32.cs',
    'HDT-HS-Reconnector\LogHandler\ConnectionLogHandler.cs',
    'HDT-HS-Reconnector\LogHandler\LogReader.cs',
    'HDT-HS-Reconnector\LogHandler\LogWatcher.cs',
    'HdtBridge\BridgeEntry.cs',
    'HdtBridge\BridgeLogger.cs',
    'HdtBridge\HdtReflection.cs',
    'HdtBridge\HdtStatePreserver.cs',
    'HdtBridge\LastOpponentOverlay.cs',
    'HdtBridge\LastOpponentTracker.cs'
) | ForEach-Object { Join-Path $repo $_ }
& $Compiler /nologo /langversion:7.3 /target:exe /platform:x64 /debug- "/out:$output\TestHarness.exe" @refs @sources (Join-Path $PSScriptRoot 'TestHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test harness compilation failed' }
& "$output\TestHarness.exe"
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

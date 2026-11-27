param(
    [string]$Version = '0.9.1',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$releaseDir = Join-Path $root ("artifacts\HDT-HS-Reconnector\Release-$Version")
$bootstrapObjDir = Join-Path $root 'artifacts\HdtBootstrap\obj'
$versionDir = Join-Path $root ("artifacts\$Version")

function Normalize-FourPartVersion([string]$v) {
    $parts = $v.Split('.')
    while ($parts.Count -lt 4) { $parts += '0' }
    return ($parts[0..3] -join '.')
}

$expectedFourPart = Normalize-FourPartVersion $Version
$versionChecks = @(
    @{ Path = (Join-Path $root 'HDT-HS-Reconnector\Properties\AssemblyInfo.cs'); Pattern = 'AssemblyVersion\("([^"]+)"\)'; Label = 'AssemblyVersion' },
    @{ Path = (Join-Path $root 'HDT-HS-Reconnector\Properties\AssemblyInfo.cs'); Pattern = 'AssemblyFileVersion\("([^"]+)"\)'; Label = 'AssemblyFileVersion' },
    @{ Path = (Join-Path $root 'HdtBridge\Properties\AssemblyInfo.cs'); Pattern = 'AssemblyVersion\("([^"]+)"\)'; Label = 'AssemblyVersion' },
    @{ Path = (Join-Path $root 'HdtBridge\Properties\AssemblyInfo.cs'); Pattern = 'AssemblyFileVersion\("([^"]+)"\)'; Label = 'AssemblyFileVersion' },
    @{ Path = (Join-Path $root 'HDT-HS-Reconnector\app.manifest'); Pattern = 'assemblyIdentity\s+version="([^"]+)"'; Label = 'assemblyIdentity' }
)
foreach ($chk in $versionChecks) {
    $content = Get-Content -LiteralPath $chk.Path -Raw
    if ($content -notmatch $chk.Pattern) {
        throw "Could not extract $($chk.Label) from $($chk.Path)"
    }
    $found = Normalize-FourPartVersion $Matches[1]
    if ($found -ne $expectedFourPart) {
        throw "Version mismatch ($($chk.Label)) in $($chk.Path): expected $expectedFourPart (for -Version $Version), found $found"
    }
}

New-Item -ItemType Directory -Force -Path $releaseDir, $bootstrapObjDir, $versionDir | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = $null
if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath 2>$null | Select-Object -First 1
}
if (-not $vsPath) {
    throw 'Visual Studio Build Tools installation was not found.'
}

$msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $msbuild)) { throw "MSBuild not found at $msbuild" }
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars" }

$netfxInc = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\NETFXSDK') -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'Include\um' } |
    Where-Object { Test-Path (Join-Path $_ 'metahost.h') } |
    Select-Object -First 1
if (-not $netfxInc) { throw 'Windows Kits NETFXSDK metahost.h was not found.' }

Write-Output '=== Building managed solution (HDT-HS-Reconnector + HdtBridge) ==='
& $msbuild (Join-Path $root 'HDT-HS-Reconnector.sln') /t:Clean,Build /p:Configuration=Release /p:Platform=x64 "/p:OutputPath=$releaseDir\" /v:minimal
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }

Write-Output '=== Building native x64 HdtBootstrap.dll ==='
$cppPath = Join-Path $root 'HdtBootstrap\HdtBootstrap.cpp'
$objPath = Join-Path $bootstrapObjDir 'HdtBootstrap.obj'
$libPath = Join-Path $bootstrapObjDir 'HdtBootstrap.lib'
$dllPath = Join-Path $releaseDir 'HdtBootstrap.dll'
$cmd = "`"$vcvars`" >nul && cl.exe /nologo /O2 /MT /W4 /EHsc /DNDEBUG /Brepro /I`"$netfxInc`" /Fo`"$objPath`" /LD `"$cppPath`" /Fe`"$dllPath`" /link /NOLOGO /DEBUG:NONE /OPT:REF /OPT:ICF /INCREMENTAL:NO /IMPLIB:`"$libPath`" ole32.lib"
cmd.exe /c $cmd
if ($LASTEXITCODE -ne 0) { throw "HdtBootstrap.dll build failed with exit code $LASTEXITCODE" }

if (-not $SkipTests) {
    Write-Output '=== Running unit tests ==='
    & (Join-Path $root 'tests\Run-Tests.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed with exit code $LASTEXITCODE" }
}

Write-Output '=== Verifying binaries ==='
$verify = Join-Path $root 'tests\Verify-Release.ps1'
& $verify -Binary (Join-Path $releaseDir 'HDT-HS-Reconnector.exe')
& $verify -Binary (Join-Path $releaseDir 'HdtBridge.dll')
& $verify -Binary (Join-Path $releaseDir 'HdtBootstrap.dll')

Write-Output "=== Packaging artifacts\$Version ==="
$files = @(
    (Join-Path $releaseDir 'HDT-HS-Reconnector.exe'),
    (Join-Path $releaseDir 'HdtBridge.dll'),
    (Join-Path $releaseDir 'HdtBootstrap.dll')
)
foreach ($f in $files) {
    Copy-Item -LiteralPath $f -Destination (Join-Path $versionDir (Split-Path $f -Leaf)) -Force
}

$zipPath = Join-Path $versionDir ("HDT-HS-Reconnector-$Version-x64.zip")
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path $files -DestinationPath $zipPath -CompressionLevel Optimal

Get-ChildItem $versionDir | Select-Object Name, Length, LastWriteTime

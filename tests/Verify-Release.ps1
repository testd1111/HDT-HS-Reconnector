param(
    [Alias('Exe')]
    [Parameter(Mandatory = $true)]
    [string]$Binary
)
$ErrorActionPreference = 'Stop'
$binPath = (Resolve-Path -LiteralPath $Binary).Path
$data = [IO.File]::ReadAllBytes($binPath)
$pe = [BitConverter]::ToInt32($data, 0x3c)
if ([BitConverter]::ToUInt16($data, $pe + 4) -ne 0x8664) { throw "Expected AMD64 binary: $binPath" }
$optional = $pe + 24
if ([BitConverter]::ToUInt16($data, $optional) -ne 0x20b) { throw "Expected PE32+ header: $binPath" }
$debugRva = [BitConverter]::ToUInt32($data, $optional + 112 + 6 * 8)
$debugSize = [BitConverter]::ToUInt32($data, $optional + 112 + 6 * 8 + 4)
$clrRva = [BitConverter]::ToUInt32($data, $optional + 112 + 14 * 8)
$sectionCount = [BitConverter]::ToUInt16($data, $pe + 6)
$sections = $optional + [BitConverter]::ToUInt16($data, $pe + 20)
if ($debugRva -ne 0) {
    $debugOffset = $null
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $section = $sections + 40 * $i
        $rva = [BitConverter]::ToUInt32($data, $section + 12)
        $size = [Math]::Max([BitConverter]::ToUInt32($data, $section + 8), [BitConverter]::ToUInt32($data, $section + 16))
        if ($debugRva -ge $rva -and $debugRva -lt $rva + $size) {
            $debugOffset = [BitConverter]::ToUInt32($data, $section + 20) + $debugRva - $rva
            break
        }
    }
    if ($null -eq $debugOffset) { throw "Invalid PE debug directory: $binPath" }
    for ($i = 0; $i -lt $debugSize; $i += 28) {
        if ([BitConverter]::ToUInt32($data, $debugOffset + $i + 12) -eq 2) { throw "Unexpected CodeView/PDB record: $binPath" }
    }
}
$texts = @([Text.Encoding]::ASCII.GetString($data), [Text.Encoding]::Unicode.GetString($data), [Text.Encoding]::Unicode.GetString($data, 1, $data.Length - 1))
$pattern = '(?i)(?<![a-z])[a-z]:[\\/][a-z0-9_ .\\/-]{2,}|\.pdb\b'
foreach ($text in $texts) {
    if ($text -match $pattern) {
        throw "Unexpected absolute path or debug marker in ${binPath}: $($Matches[0])"
    }
}
if (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($binPath, '.pdb'))) { throw "Unexpected adjacent PDB: $binPath" }
$version = if ($clrRva -ne 0) { [Reflection.AssemblyName]::GetAssemblyName($binPath).Version.ToString() } else { 'native' }
[pscustomobject]@{
    File = Split-Path $binPath -Leaf
    Version = $version
    Architecture = 'x64'
    SizeBytes = $data.Length
    CodeView = 'absent'
    AbsolutePathMarkers = 'absent'
    SHA256 = (Get-FileHash -LiteralPath $binPath -Algorithm SHA256).Hash
}

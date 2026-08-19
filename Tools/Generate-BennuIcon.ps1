# =====================================================================================
#  Generate-BennuIcon.ps1
#
#  Compiles Tools\BennuIconGen.cs (+ Dds.cs and BennuMapGen.cs, for Cfg and the DDS
#  writer) and writes the map-view node icon to GameData\Bennu\Icons\Bennu_Icon.dds,
#  plus a PNG preview in Tools\preview\.
#
#  Usage:   .\Tools\Generate-BennuIcon.ps1
#
#  The silhouette is derived from the same Cfg shape constants the body itself uses, so
#  reshaping the body reshapes the icon. Output is deterministic.
#
#  NOTE ON THE OUTPUT LOCATION
#  ---------------------------
#  This is the one texture in the pack that does NOT live in PluginData. Kopernicus'
#  Texture2DParser tries GameDatabase.ExistsTexture first and only then falls back to
#  loading off disk, and KSP's loader skips PluginData when building the GameDatabase.
#  Putting the icon in a normal folder keeps it on the reliable path, and it is 16 KB,
#  so the extra GameDatabase entry costs nothing.
#
#  Requires only Windows PowerShell 5.1 + .NET Framework.
# =====================================================================================

[CmdletBinding()]
param(
    [string] $OutDir,
    [string] $PreviewDir
)

$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root     = Split-Path -Parent $toolsDir
if (-not $OutDir)     { $OutDir     = Join-Path $root 'GameData\Bennu\Icons' }
if (-not $PreviewDir) { $PreviewDir = Join-Path $toolsDir 'preview' }

Write-Host ''
Write-Host '=== Bennu map icon generator ===' -ForegroundColor Cyan
Write-Host "  output: $OutDir"
Write-Host ''

$sources = @(
    (Join-Path $toolsDir 'BennuIconGen.cs'),
    (Join-Path $toolsDir 'BennuMapGen.cs'),
    (Join-Path $toolsDir 'Dds.cs')
)
foreach ($s in $sources) {
    if (-not (Test-Path $s)) { throw "Missing source file: $s" }
}

Add-Type -AssemblyName System.Drawing

if (-not ([System.Management.Automation.PSTypeName]'BennuGen.IconGen').Type) {
    Write-Host '  compiling...' -ForegroundColor DarkGray
    Add-Type -Path $sources `
             -ReferencedAssemblies @('System.Drawing', 'System.Core') `
             -ErrorAction Stop
} else {
    Write-Host '  (types already loaded in this session - reusing)' -ForegroundColor DarkGray
}

[BennuGen.IconGen]::Run($OutDir, $PreviewDir)

Write-Host ''
Get-ChildItem $OutDir | Sort-Object Name |
    Select-Object Name, @{n = 'Size'; e = { '{0,8:N0} KB' -f ($_.Length / 1KB) } } |
    Format-Table -AutoSize

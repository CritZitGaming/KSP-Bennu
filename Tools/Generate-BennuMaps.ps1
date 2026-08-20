# =====================================================================================
#  Generate-BennuMaps.ps1
#
#  Compiles Tools\BennuMapGen.cs + Tools\Dds.cs and writes the shape model, colour,
#  normal and biome maps into Bennu\PluginData.
#
#  Usage:   .\Tools\Generate-BennuMaps.ps1
#
#  Output is deterministic - every seed is fixed - so re-running reproduces the same
#  bytes. Change the tunables in the Cfg class of BennuMapGen.cs to reshape the body,
#  then re-run and copy the radius/deformity numbers it prints into Bennu.cfg (they are
#  also written to PluginData\Bennu_Derived.cfg).
#
#  Requires only Windows PowerShell 5.1 + .NET Framework - no external tooling.
# =====================================================================================

[CmdletBinding()]
param(
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root     = Split-Path -Parent $toolsDir
if (-not $OutDir) { $OutDir = Join-Path $root 'Bennu\PluginData' }

Write-Host ''
Write-Host '=== Bennu map generator ===' -ForegroundColor Cyan
Write-Host "  output: $OutDir"
Write-Host ''

$sources = @(
    (Join-Path $toolsDir 'BennuMapGen.cs'),
    (Join-Path $toolsDir 'Dds.cs')
)
foreach ($s in $sources) {
    if (-not (Test-Path $s)) { throw "Missing source file: $s" }
}

Add-Type -AssemblyName System.Drawing

# Compile fresh into this session. Add-Type refuses to redefine a type in a session
# that already loaded it, so a rebuild after editing the .cs needs a new shell.
if (-not ([System.Management.Automation.PSTypeName]'BennuGen.Generator').Type) {
    Write-Host '  compiling...' -ForegroundColor DarkGray
    Add-Type -Path $sources `
             -ReferencedAssemblies @('System.Drawing', 'System.Core') `
             -ErrorAction Stop
} else {
    Write-Host '  (types already loaded in this session - reusing)' -ForegroundColor DarkGray
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$sw = [System.Diagnostics.Stopwatch]::StartNew()
[BennuGen.Generator]::Run($OutDir)
$sw.Stop()

Write-Host ''
Write-Host ("  finished in {0:N1}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host ''
Get-ChildItem $OutDir | Sort-Object Name |
    Select-Object Name, @{n = 'Size'; e = { '{0,8:N0} KB' -f ($_.Length / 1KB) } } |
    Format-Table -AutoSize

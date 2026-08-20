# =====================================================================================
#  Preview-BennuMaps.ps1
#
#  Reads the generated DDS files back, decodes them with an independent decoder, and
#  writes PNG previews plus shaded 3D renders of the shape model.
#
#  This is the stand-in for launching KSP: if the previews decode cleanly and the
#  globe renders look like Bennu, the DDS headers, block layout and orientation are
#  right. Previews land in Tools\preview\ and are NOT part of the shipped mod.
# =====================================================================================

[CmdletBinding()]
param(
    [double] $Datum,
    [double] $Deformity
)

$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root     = Split-Path -Parent $toolsDir
$dataDir  = Join-Path $root 'Bennu\PluginData'
$outDir   = Join-Path $toolsDir 'preview'

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Add-Type -AssemblyName System.Drawing
if (-not ([System.Management.Automation.PSTypeName]'BennuGen.Preview').Type) {
    Add-Type -Path (Join-Path $toolsDir 'DdsPreview.cs') `
             -ReferencedAssemblies @('System.Drawing') -ErrorAction Stop
}

# Pull the authoritative datum/deformity out of the generator's own output unless the
# caller overrode them, so the render can never disagree with the shipped maps.
if (-not $Datum -or -not $Deformity) {
    $derived = Join-Path $dataDir 'Bennu_Derived.cfg'
    if (Test-Path $derived) {
        $txt = Get-Content $derived -Raw
        if ($txt -match 'Properties/radius\s+\(datum\)\s*:\s*([0-9.]+)')   { $Datum     = [double]$Matches[1] }
        if ($txt -match 'VertexHeightMap/deformity\s*:\s*([0-9.]+)')       { $Deformity = [double]$Matches[1] }
    }
}
if (-not $Datum)     { $Datum     = 939 }
if (-not $Deformity) { $Deformity = 216 }

Write-Host ''
Write-Host '=== DDS headers ===' -ForegroundColor Cyan
Get-ChildItem $dataDir -Filter *.dds | Sort-Object Name | ForEach-Object {
    Write-Host ('  ' + [BennuGen.Preview]::Describe($_.FullName))
}

Write-Host ''
Write-Host '=== decoding ===' -ForegroundColor Cyan
$height = [BennuGen.Preview]::Load((Join-Path $dataDir 'Bennu_Height.dds'))
$color  = [BennuGen.Preview]::Load((Join-Path $dataDir 'Bennu_Color.dds'))
$normal = [BennuGen.Preview]::Load((Join-Path $dataDir 'Bennu_Normal.dds'))
$biome  = [BennuGen.Preview]::Load((Join-Path $dataDir 'Bennu_Biome.dds'))
Write-Host '  all four decoded OK'

[BennuGen.Preview]::SavePng($height, (Join-Path $outDir 'map_height.png'), 1400)
[BennuGen.Preview]::SavePng($color,  (Join-Path $outDir 'map_color.png'),  1400)
[BennuGen.Preview]::SavePng($normal, (Join-Path $outDir 'map_normal.png'), 1400)
[BennuGen.Preview]::SavePng($biome,  (Join-Path $outDir 'map_biome.png'),  1400)

Write-Host ''
Write-Host '=== terrain slope (is it landable?) ===' -ForegroundColor Cyan
Write-Host ([BennuGen.Preview]::SlopeStats($height, $Datum, $Deformity))

Write-Host ''
Write-Host "=== rendering globes (datum=$Datum deformity=$Deformity) ===" -ForegroundColor Cyan
# Equatorial view shows the ridge in profile; polar view shows the diamond outline.
[BennuGen.Preview]::RenderGlobe($height, $color, (Join-Path $outDir 'globe_equator_000.png'), 700, $Datum, $Deformity,   0, 0)
[BennuGen.Preview]::RenderGlobe($height, $color, (Join-Path $outDir 'globe_equator_120.png'), 700, $Datum, $Deformity, 120, 0)
[BennuGen.Preview]::RenderGlobe($height, $color, (Join-Path $outDir 'globe_equator_240.png'), 700, $Datum, $Deformity, 240, 0)
[BennuGen.Preview]::RenderGlobe($height, $color, (Join-Path $outDir 'globe_tilt30.png'),      700, $Datum, $Deformity,  60, 30)
[BennuGen.Preview]::RenderGlobe($height, $color, (Join-Path $outDir 'globe_pole.png'),        700, $Datum, $Deformity,   0, 85)

Write-Host ''
Get-ChildItem $outDir | Sort-Object Name |
    Select-Object Name, @{n='Size'; e={'{0,7:N0} KB' -f ($_.Length/1KB)}} | Format-Table -AutoSize

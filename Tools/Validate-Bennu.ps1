# =====================================================================================
#  Validate-Bennu.ps1
#
#  Static checks that catch the mistakes which otherwise only show up as a silent
#  failure or a wall of exceptions in KSP.log:
#
#    1. Brace balance in every .cfg
#    2. Kopernicus node keys checked against the ParserTarget names reflected out of
#       the installed Kopernicus.dll (catches typos and version drift)
#    3. Every referenced texture / model path actually resolves, in this pack or in
#       the live GameData
#    4. Biome colours in Bennu.cfg match the generated biome map byte-for-byte
#    5. Height-map deformity in Bennu.cfg agrees with what the generator produced
#    6. Derived orbital and surface physics, printed for sanity
#
#  Usage:  .\Tools\Validate-Bennu.ps1  [-GameData "<path to KSP GameData>"]
# =====================================================================================

[CmdletBinding()]
param(
    [string] $GameData = "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\GameData"
)

$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root     = Split-Path -Parent $toolsDir
$packData = Join-Path $root 'GameData'
$cfgDir   = Join-Path $packData 'Bennu'

$script:Errors   = New-Object System.Collections.Generic.List[string]
$script:Warnings = New-Object System.Collections.Generic.List[string]

function Fail($m) { $script:Errors.Add($m)   | Out-Null }
function Warn($m) { $script:Warnings.Add($m) | Out-Null }

function Section($t) {
    Write-Host ''
    Write-Host "=== $t ===" -ForegroundColor Cyan
}

# -------------------------------------------------------------------------------------
#  1. Brace balance
# -------------------------------------------------------------------------------------
Section '1. Brace balance'
$cfgs = Get-ChildItem $cfgDir -Recurse -Filter *.cfg
foreach ($f in $cfgs) {
    $depth = 0; $line = 0; $bad = $false
    foreach ($l in (Get-Content $f.FullName)) {
        $line++
        $code = $l -replace '//.*$', ''
        $depth += ([regex]::Matches($code, '\{')).Count
        $depth -= ([regex]::Matches($code, '\}')).Count
        if ($depth -lt 0) { Fail "$($f.Name): unbalanced '}' at line $line"; $bad = $true; break }
    }
    if (-not $bad) {
        if ($depth -ne 0) { Fail "$($f.Name): $depth unclosed '{'" }
        else { Write-Host ("  OK   {0}" -f $f.Name) -ForegroundColor DarkGray }
    }
}

# -------------------------------------------------------------------------------------
#  2. Kopernicus key check
# -------------------------------------------------------------------------------------
Section '2. Kopernicus config keys'

$kopDll = Join-Path $GameData 'Kopernicus\Plugins\Kopernicus.dll'
$knownKeys = $null
if (Test-Path $kopDll) {
    $managed = Join-Path (Split-Path -Parent $GameData) 'KSP_x64_Data\Managed'
    $plugins = Split-Path -Parent $kopDll
    $dirs = @($managed, $plugins)
    $handler = [System.ResolveEventHandler] {
        param($s, $e)
        $simple = $e.Name.Split(',')[0]
        foreach ($d in $dirs) {
            $p = Join-Path $d ($simple + '.dll')
            if (Test-Path $p) { try { return [System.Reflection.Assembly]::LoadFrom($p) } catch { } }
        }
        return $null
    }
    [System.AppDomain]::CurrentDomain.add_AssemblyResolve($handler)
    try {
        $asm = [System.Reflection.Assembly]::LoadFrom($kopDll)
        $types = @()
        try { $types = $asm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] {
            $types = $_.Exception.Types | Where-Object { $_ -ne $null }
        }
        $flags = [System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly'
        $set = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($t in $types) {
            $members = @()
            try { $members += $t.GetFields($flags) }     catch {}
            try { $members += $t.GetProperties($flags) } catch {}
            foreach ($m in $members) {
                $attrs = @(); try { $attrs = $m.GetCustomAttributes($false) } catch { continue }
                foreach ($a in $attrs) {
                    if ($a.GetType().Name -notmatch '^ParserTarget') { continue }
                    $key = $null; $at = $a.GetType()
                    foreach ($n in @('FieldName','fieldName','Name','name')) {
                        if (-not [string]::IsNullOrEmpty($key)) { break }
                        $pi = $at.GetProperty($n); if ($pi) { try { $key = $pi.GetValue($a,$null) } catch {} }
                        if ([string]::IsNullOrEmpty($key)) { $fi = $at.GetField($n); if ($fi) { try { $key = $fi.GetValue($a) } catch {} } }
                    }
                    if ([string]::IsNullOrEmpty($key)) { $key = $m.Name }
                    [void]$set.Add([string]$key)
                }
            }
        }
        $knownKeys = $set
        Write-Host "  reflected $($set.Count) parser keys from Kopernicus.dll" -ForegroundColor DarkGray
    } catch {
        Warn "could not reflect Kopernicus.dll ($($_.Exception.Message)); key check skipped"
    }
} else {
    Warn "Kopernicus.dll not found at $kopDll; key check skipped"
}

if ($knownKeys) {
    # Node names and MM operators are not parser keys; only check `key = value` lines
    # inside the Kopernicus body config.
    $ignore = @('name','enabled','order','value','color','displayName','key')
    $bennuCfg = Join-Path $cfgDir 'Configs\Bennu.cfg'
    $ln = 0
    foreach ($l in (Get-Content $bennuCfg)) {
        $ln++
        $code = ($l -replace '//.*$', '').Trim()
        if ($code -notmatch '^([%@+\-*!]?)([A-Za-z_][A-Za-z0-9_]*)\s*=') { continue }
        $k = $Matches[2]
        if ($ignore -contains $k) { continue }
        if (-not $knownKeys.Contains($k)) {
            Fail "Bennu.cfg line ${ln}: '$k' is not a Kopernicus parser key"
        }
    }
    if ($script:Errors.Count -eq 0) { Write-Host '  all keys in Bennu.cfg recognised' -ForegroundColor DarkGray }
}

# -------------------------------------------------------------------------------------
#  3. Referenced asset paths
# -------------------------------------------------------------------------------------
Section '3. Asset references'

$refs = @{}
foreach ($f in $cfgs) {
    $ln = 0
    foreach ($l in (Get-Content $f.FullName)) {
        $ln++
        $code = ($l -replace '//.*$', '')
        if ($code -notmatch '=\s*(\S+\.(dds|png|mu|bin))\s*$') { continue }
        $p = $Matches[1]
        if (-not $refs.ContainsKey($p)) { $refs[$p] = @() }
        $refs[$p] += "$($f.Name):$ln"
    }
    # Parallax scatter models are referenced without a file extension.
    $ln = 0
    foreach ($l in (Get-Content $f.FullName)) {
        $ln++
        $code = ($l -replace '//.*$', '')
        if ($code -notmatch '^\s*model\s*=\s*(\S+)\s*$') { continue }
        $p = $Matches[1] + '.mu'
        if (-not $refs.ContainsKey($p)) { $refs[$p] = @() }
        $refs[$p] += "$($f.Name):$ln"
    }
    # So is the map node icon: iconTexture is looked up through GameDatabase, which
    # indexes textures by extensionless URL.
    $ln = 0
    foreach ($l in (Get-Content $f.FullName)) {
        $ln++
        $code = ($l -replace '//.*$', '')
        if ($code -notmatch '^\s*iconTexture\s*=\s*(\S+)\s*$') { continue }
        $p = $Matches[1] + '.dds'
        if (-not $refs.ContainsKey($p)) { $refs[$p] = @() }
        $refs[$p] += "$($f.Name):$ln"
    }
}

# Parallax no longer ships its terrain and scatter textures as loose files - they are
# inside parallax-stock-terrain-textures.unity3d and friends, and KSPTextureLoader
# resolves the old PluginData paths out of the bundle at load time. Parallax's own
# Terrain.cfg still references them exactly the way this pack does, so a path that does
# not exist on disk is only a real error if the owning mod has no bundle to serve it.
function Test-AssetBundlePath([string] $relPath) {
    $parts = $relPath -split '[\\/]'
    if ($parts.Count -lt 1) { return $false }
    $modDir = Join-Path $GameData $parts[0]
    if (-not (Test-Path $modDir)) { return $false }
    return @(Get-ChildItem -Path $modDir -Filter '*.unity3d' -File -ErrorAction SilentlyContinue).Count -gt 0
}

foreach ($p in ($refs.Keys | Sort-Object)) {
    $inPack = Join-Path $packData $p
    $inGame = Join-Path $GameData $p
    if (Test-Path $inPack)      { Write-Host ("  OK   (pack) {0}" -f $p) -ForegroundColor DarkGray }
    elseif (Test-Path $inGame)  { Write-Host ("  OK   (game) {0}" -f $p) -ForegroundColor DarkGray }
    elseif ($p -like '*Cache*') { Write-Host ("  --   (generated at runtime) {0}" -f $p) -ForegroundColor DarkGray }
    elseif (Test-AssetBundlePath $p) {
        Write-Host ("  OK   (bundle) {0}" -f $p) -ForegroundColor DarkGray
    }
    else { Fail "missing asset: $p  (referenced by $($refs[$p] -join ', '))" }
}

# -------------------------------------------------------------------------------------
#  4 + 5. Biome colours and height deformity vs. the generated maps
# -------------------------------------------------------------------------------------
Section '4. Biome colours vs. Bennu_Biome.dds'

Add-Type -AssemblyName System.Drawing
if (-not ([System.Management.Automation.PSTypeName]'BennuGen.Preview').Type) {
    Add-Type -Path (Join-Path $toolsDir 'DdsPreview.cs') -ReferencedAssemblies @('System.Drawing')
}

$biomeDds = Join-Path $cfgDir 'PluginData\Bennu_Biome.dds'
if (Test-Path $biomeDds) {
    $img = [BennuGen.Preview]::Load($biomeDds)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    for ($i = 0; $i -lt ($img.W * $img.H); $i++) {
        [void]$seen.Add(('{0},{1},{2}' -f $img.Rgba[$i*4], $img.Rgba[$i*4+1], $img.Rgba[$i*4+2]))
    }
    Write-Host "  map contains $($seen.Count) distinct colours"

    # Pull the declared biome colours (0-1 floats) out of Bennu.cfg.
    $declared = @{}
    $txt = Get-Content (Join-Path $cfgDir 'Configs\Bennu.cfg') -Raw
    $rx = [regex]'(?s)Biome\s*\{(.*?)\}'
    foreach ($m in $rx.Matches($txt)) {
        $blk = $m.Groups[1].Value
        if ($blk -match 'name\s*=\s*([^\r\n]+)' ) { $bn = $Matches[1].Trim() } else { continue }
        if ($blk -match 'color\s*=\s*([0-9.]+),([0-9.]+),([0-9.]+)') {
            $r = [int][math]::Round([double]$Matches[1] * 255)
            $g = [int][math]::Round([double]$Matches[2] * 255)
            $b = [int][math]::Round([double]$Matches[3] * 255)
            $declared[$bn] = "$r,$g,$b"
        }
    }
    Write-Host "  Bennu.cfg declares $($declared.Count) biomes"

    foreach ($bn in ($declared.Keys | Sort-Object)) {
        if ($seen.Contains($declared[$bn])) {
            Write-Host ("  OK   {0,-22} {1}" -f $bn, $declared[$bn]) -ForegroundColor DarkGray
        } else {
            Fail "biome '$bn' declares colour $($declared[$bn]) but no such pixel exists in Bennu_Biome.dds"
        }
    }
    foreach ($c in $seen) {
        if ($declared.Values -notcontains $c) {
            Fail "Bennu_Biome.dds contains colour $c with no matching Biome entry in Bennu.cfg"
        }
    }
} else {
    Fail "Bennu_Biome.dds not found - run Generate-BennuMaps.ps1 first"
}

Section '5. Geometry agreement'
$derived = Join-Path $cfgDir 'PluginData\Bennu_Derived.cfg'
if (Test-Path $derived) {
    $d = Get-Content $derived -Raw
    $genDatum = if ($d -match 'Properties/radius\s+\(datum\)\s*:\s*([0-9.]+)') { [double]$Matches[1] } else { $null }
    $genDef   = if ($d -match 'VertexHeightMap/deformity\s*:\s*([0-9.]+)')     { [double]$Matches[1] } else { $null }

    $b = Get-Content (Join-Path $cfgDir 'Configs\Bennu.cfg') -Raw
    $cfgRadius = if ($b -match '(?m)^\s*radius\s*=\s*([0-9.]+)')      { [double]$Matches[1] } else { $null }
    $cfgDef    = if ($b -match '(?m)^\s*deformity\s*=\s*([0-9.]+)')   { [double]$Matches[1] } else { $null }
    $cfgMapMax = if ($b -match '(?m)^\s*mapMaxHeight\s*=\s*([0-9.]+)'){ [double]$Matches[1] } else { $null }

    if ($cfgRadius -ne $genDatum) { Fail "Bennu.cfg radius=$cfgRadius but the maps were built for datum $genDatum" }
    else { Write-Host "  OK   radius        = $cfgRadius" -ForegroundColor DarkGray }

    if ($cfgDef -ne $genDef) { Fail "Bennu.cfg VertexHeightMap deformity=$cfgDef but the maps imply $genDef" }
    else { Write-Host "  OK   deformity     = $cfgDef" -ForegroundColor DarkGray }

    if ($cfgMapMax -ne $genDef) { Warn "mapMaxHeight=$cfgMapMax does not equal deformity $genDef" }
    else { Write-Host "  OK   mapMaxHeight  = $cfgMapMax" -ForegroundColor DarkGray }

    # Parallax scaled altitude band must span the same range.
    $px = Get-Content (Join-Path $cfgDir 'Compatibility\Bennu_Parallax.cfg') -Raw
    $pMax = if ($px -match 'maxTerrainAltitude\s*=\s*([0-9.]+)') { [double]$Matches[1] } else { $null }
    if ($pMax -ne $genDef) { Fail "Parallax maxTerrainAltitude=$pMax but deformity is $genDef" }
    else { Write-Host "  OK   Parallax maxTerrainAltitude = $pMax" -ForegroundColor DarkGray }
}

# -------------------------------------------------------------------------------------
#  5b. Terrain slope - the "can you actually land on it" check
# -------------------------------------------------------------------------------------
Section '5b. Terrain slope'

$heightDds = Join-Path $cfgDir 'PluginData\Bennu_Height.dds'
if ((Test-Path $heightDds) -and $null -ne $genDatum -and $null -ne $genDef) {
    $himg = [BennuGen.Preview]::Load($heightDds)
    Write-Host ([BennuGen.Preview]::SlopeStats($himg, $genDatum, $genDef))

    # Worst-case facet baked in by 8-bit quantisation. VertexHeightMap cannot use a
    # 16-bit map (Kopernicus parses it as MapSOParserGreyScale), so the only lever is
    # texel size - see the note in BennuMapGen.cs on Cfg.HeightOutW.
    $step = $genDef / 255.0
    $texel = 2 * [math]::PI * ($genDatum + $genDef * 0.3) / $himg.W
    $facet = [math]::Atan($step / $texel) * 180 / [math]::PI
    Write-Host ("  quantisation: {0:N3} m step over {1:N2} m texels = {2:N1} deg worst-case facet" -f $step, $texel, $facet)

    if ($facet -gt 15) {
        Fail ("8-bit height quantisation alone produces {0:N1} deg facets - lower the height map resolution or the deformity" -f $facet)
    } elseif ($facet -gt 10) {
        Warn ("quantisation facets of {0:N1} deg will be visible as terracing" -f $facet)
    } else {
        Write-Host ("  OK   {0:N1} deg is below the ~11 deg Gilly ships with" -f $facet) -ForegroundColor DarkGray
    }
} else {
    Warn 'height map or derived values missing; slope check skipped'
}

# -------------------------------------------------------------------------------------
#  6. Derived physics
# -------------------------------------------------------------------------------------
Section '6. Derived physics'
$b = Get-Content (Join-Path $cfgDir 'Configs\Bennu.cfg') -Raw
$R    = [double]([regex]::Match($b, '(?m)^\s*radius\s*=\s*([0-9.]+)').Groups[1].Value)
$gee  = [double]([regex]::Match($b, '(?m)^\s*geeASL\s*=\s*([0-9.eE\-]+)').Groups[1].Value)
$sma  = [double]([regex]::Match($b, '(?m)^\s*semiMajorAxis\s*=\s*([0-9.]+)').Groups[1].Value)
$ecc  = [double]([regex]::Match($b, '(?m)^\s*eccentricity\s*=\s*([0-9.]+)').Groups[1].Value)
$rot  = [double]([regex]::Match($b, '(?m)^\s*rotationPeriod\s*=\s*([0-9.]+)').Groups[1].Value)

$g     = $gee * 9.80665
$GM    = $g * $R * $R
$vEsc  = [math]::Sqrt(2 * $GM / $R)
$mass  = $GM / 6.67408e-11
$sunM  = 1.7565459e28
$soi   = $sma * [math]::Pow($mass / $sunM, 0.4)
$sunGM = 1.1723328e18
$per   = 2 * [math]::PI * [math]::Sqrt([math]::Pow($sma,3) / $sunGM)
$kerbinSMA = 13599840256.0
$kerbinYr  = 9203544.6

Write-Host ("  datum radius        {0,14:N0} m" -f $R)
Write-Host ("  surface gravity     {0,14:N4} m/s^2   ({1} g)" -f $g, $gee)
Write-Host ("  GM                  {0,14:N0} m^3/s^2" -f $GM)
Write-Host ("  escape velocity     {0,14:N2} m/s" -f $vEsc)
Write-Host ("  orbital v @1.2km    {0,14:N2} m/s" -f ([math]::Sqrt($GM / 1200)))
Write-Host ("  sphere of influence {0,14:N0} m   ({1:N1} x datum radius)" -f $soi, ($soi / $R))
Write-Host ("  rotation period     {0,14:N1} s   ({1:N3} h)" -f $rot, ($rot / 3600))
Write-Host ("  periapsis           {0,14:N0} m   ({1})" -f ($sma*(1-$ecc)), $(if ($sma*(1-$ecc) -lt $kerbinSMA) {'inside Kerbin'} else {'outside Kerbin'}))
Write-Host ("  apoapsis            {0,14:N0} m   ({1})" -f ($sma*(1+$ecc)), $(if ($sma*(1+$ecc) -gt $kerbinSMA) {'outside Kerbin'} else {'inside Kerbin'}))
Write-Host ("  orbital period      {0,14:N0} s   ({1:N3} Kerbin years)" -f $per, ($per / $kerbinYr))

if ($soi -lt $R * 3)      { Fail "sphere of influence ($([math]::Round($soi))m) is dangerously close to the body radius" }
$warpLimits = [regex]::Match($b, 'timewarpAltitudeLimits\s*=\s*([0-9 ]+)').Groups[1].Value -split '\s+'
if ($warpLimits.Count -gt 0) {
    $maxWarp = [double]($warpLimits[-1])
    if ($maxWarp -gt $soi) { Fail "highest timewarp altitude ($maxWarp m) is outside the SOI ($([math]::Round($soi)) m)" }
    else { Write-Host ("  OK   top warp tier {0:N0} m sits inside the {1:N0} m SOI" -f $maxWarp, $soi) -ForegroundColor DarkGray }
}

# -------------------------------------------------------------------------------------
#  7. Resource definitions
# -------------------------------------------------------------------------------------
Section '7. Resources'

$resCfg = Join-Path $cfgDir 'Compatibility\Bennu_Resources.cfg'
if (Test-Path $resCfg) {
    $rtxt = Get-Content $resCfg -Raw

    # Biome names used by BIOME_RESOURCE must exist in Bennu.cfg's Biomes list.
    $declaredBiomes = @()
    $btxt = Get-Content (Join-Path $cfgDir 'Configs\Bennu.cfg') -Raw
    foreach ($m in ([regex]'(?s)Biome\s*\{(.*?)\}').Matches($btxt)) {
        if ($m.Groups[1].Value -match 'name\s*=\s*([^\r\n]+)') { $declaredBiomes += $Matches[1].Trim() }
    }

    $usedBiomes = @()
    foreach ($m in ([regex]'(?m)^\s*BiomeName\s*=\s*(.+?)\s*$').Matches($rtxt)) {
        $usedBiomes += $m.Groups[1].Value.Trim()
    }
    $usedBiomes = $usedBiomes | Select-Object -Unique

    foreach ($ub in $usedBiomes) {
        if ($declaredBiomes -contains $ub) {
            Write-Host ("  OK   biome '{0}'" -f $ub) -ForegroundColor DarkGray
        } else {
            Fail "BIOME_RESOURCE targets biome '$ub' which is not declared in Bennu.cfg"
        }
    }

    # Resource names must actually exist (stock Ore, or a CRP RESOURCE_DEFINITION).
    $known = @('Ore')
    $crp = Join-Path $GameData 'CommunityResourcePack\CommonResources.cfg'
    if (Test-Path $crp) {
        foreach ($m in ([regex]'(?m)^\s*name\s*=\s*(\w+)\s*$').Matches((Get-Content $crp -Raw))) {
            $known += $m.Groups[1].Value
        }
    } else {
        Warn 'CommunityResourcePack not found; resource-name check limited to stock Ore'
    }

    $usedRes = @()
    foreach ($m in ([regex]'(?m)^\s*ResourceName\s*=\s*(\w+)\s*$').Matches($rtxt)) { $usedRes += $m.Groups[1].Value }
    $usedRes = $usedRes | Select-Object -Unique

    foreach ($r in $usedRes) {
        if ($known -contains $r) { Write-Host ("  OK   resource '{0}'" -f $r) -ForegroundColor DarkGray }
        else { Fail "resource '$r' is not defined by stock or CommunityResourcePack" }
    }

    # Every resource should also have a SCANsat display cutoff, or its map renders on
    # stock defaults that do not match these abundances.
    $scanCfg = Join-Path $cfgDir 'Compatibility\Bennu_SCANsat.cfg'
    if (Test-Path $scanCfg) {
        $stxt = Get-Content $scanCfg -Raw
        $scanRes = @()
        foreach ($m in ([regex]'(?m)^\s*resourceName\s*=\s*(\w+)\s*$').Matches($stxt)) { $scanRes += $m.Groups[1].Value }
        $scanRes = $scanRes | Select-Object -Unique
        foreach ($r in $usedRes) {
            if ($scanRes -notcontains $r) { Warn "resource '$r' has no SCANsat cutoff entry; its map will use stock defaults" }
        }
        foreach ($r in $scanRes) {
            if ($usedRes -notcontains $r) { Fail "SCANsat declares cutoffs for '$r' but Bennu_Resources.cfg never defines it" }
        }
        Write-Host ("  {0} resources defined, {1} with SCANsat cutoffs" -f $usedRes.Count, $scanRes.Count)

        # An @Item[X] patch against a resource SCANsat does not ship fails silently -
        # no error, no cutoffs, and the map quietly uses defaults. Check the targets
        # actually exist in SCANsat's own colour config.
        $scanColors = Join-Path $GameData 'SCANsat\Resources\SCANcolors.cfg'
        if (Test-Path $scanColors) {
            $sc = Get-Content $scanColors -Raw
            $scanKnown = @()
            $resSection = [regex]::Match($sc, '(?s)SCANsat_Resources\s*\{(.*)$')
            if ($resSection.Success) {
                foreach ($m in ([regex]'(?m)^\s*name\s*=\s*(\w+)\s*$').Matches($resSection.Groups[1].Value)) {
                    $scanKnown += $m.Groups[1].Value
                }
            }
            foreach ($r in $scanRes) {
                if ($scanKnown -contains $r) { Write-Host ("  OK   SCANsat ships a '{0}' resource node" -f $r) -ForegroundColor DarkGray }
                else { Fail "Bennu_SCANsat.cfg patches @Item[$r] but SCANsat ships no such resource node - the patch would do nothing" }
            }
        } else {
            Warn 'SCANsat/Resources/SCANcolors.cfg not found; patch-target check skipped'
        }

        # The altimetry range has to span the actual terrain or the map is one flat colour.
        $sMax = if ($stxt -match 'maxHeightRange\s*=\s*([0-9.]+)') { [double]$Matches[1] } else { $null }
        $sMin = if ($stxt -match 'minHeightRange\s*=\s*([0-9.]+)') { [double]$Matches[1] } else { $null }
        if ($null -ne $sMax -and $null -ne $genDef) {
            if ($sMax -lt $genDef)      { Fail "SCANsat maxHeightRange ($sMax) is below the terrain maximum ($genDef) - high ground will clip" }
            elseif ($sMax -gt $genDef * 3) { Warn "SCANsat maxHeightRange ($sMax) is far above the terrain maximum ($genDef) - map will look flat" }
            else { Write-Host ("  OK   altimetry range {0}-{1} m spans the {2} m of relief" -f $sMin, $sMax, $genDef) -ForegroundColor DarkGray }
        }
    } else {
        Warn 'Bennu_SCANsat.cfg not found'
    }
}

# -------------------------------------------------------------------------------------
Section 'Result'
if ($script:Warnings.Count) {
    foreach ($w in $script:Warnings) { Write-Host "  WARN  $w" -ForegroundColor Yellow }
}
if ($script:Errors.Count) {
    foreach ($e in $script:Errors) { Write-Host "  ERROR $e" -ForegroundColor Red }
    Write-Host ''
    Write-Host "  $($script:Errors.Count) error(s)" -ForegroundColor Red
    exit 1
}
Write-Host '  All checks passed.' -ForegroundColor Green
Write-Host ''

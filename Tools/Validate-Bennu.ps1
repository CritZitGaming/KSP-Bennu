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
#   5b. Terrain slope, including the facets 8-bit height quantisation bakes in
#   5c. Prepared landing pads still sit on the terrain the height map actually has
#    6. Derived orbital and surface physics, printed for sanity
#    7. Resources and their SCANsat display cutoffs
#    8. Contract types, biomes and waypoints checked against the installed
#       ContractConfigurator assembly and against the pad they point at
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
$packData = $root
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
    # Every pack file that writes into the Kopernicus node, not just the body definition.
    $kopCfgs = @('Configs\Bennu.cfg', 'Configs\Bennu_Landmarks.cfg')
    foreach ($rel in $kopCfgs) {
        $f = Join-Path $cfgDir $rel
        if (-not (Test-Path $f)) { Fail "$rel is missing"; continue }
        $leaf = Split-Path -Leaf $f
        $ln = 0
        foreach ($l in (Get-Content $f)) {
            $ln++
            $code = ($l -replace '//.*$', '').Trim()
            if ($code -notmatch '^([%@+\-*!]?)([A-Za-z_][A-Za-z0-9_]*)\s*=') { continue }
            $k = $Matches[2]
            if ($ignore -contains $k) { continue }
            if (-not $knownKeys.Contains($k)) {
                Fail "${leaf} line ${ln}: '$k' is not a Kopernicus parser key"
            }
        }
    }
    if ($script:Errors.Count -eq 0) {
        Write-Host "  all keys in $($kopCfgs.Count) Kopernicus configs recognised" -ForegroundColor DarkGray
    }
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
#  5c. Landmark pads - does the prepared surface still sit on the terrain?
#
#  A FlattenArea is a fixed altitude written into a config; the terrain under it comes
#  from a regenerated map. Change the generator and the pad silently becomes a mesa or a
#  pit with nothing to catch it. This re-samples the shipped height map underneath every
#  pad and checks the flattenTo is still inside the local relief.
# -------------------------------------------------------------------------------------
Section '5c. Landmark pads'

$lmCfg = Join-Path $cfgDir 'Configs\Bennu_Landmarks.cfg'
if ((Test-Path $lmCfg) -and $himg -and $null -ne $genDatum -and $null -ne $genDef) {

    # Same lat/lon -> texel mapping as BennuMapGen.BuildBiomeMap, row 0 = south pole.
    function Get-DiscHeights($img, $lat, $lon, $radiusM, $datum, $deformity) {
        $circ  = 2 * [math]::PI * $datum
        $mX    = ($circ / $img.W) * [math]::Cos($lat * [math]::PI / 180)
        $mY    = ($circ / 2) / $img.H
        $rX    = [math]::Max(1, [int][math]::Ceiling($radiusM / $mX))
        $rY    = [math]::Max(1, [int][math]::Ceiling($radiusM / $mY))
        $j0    = [int][math]::Floor((($lat / 180.0) + 0.5) * $img.H)
        $i0    = [int][math]::Floor(((($lon + 540.0) % 360.0) / 360.0) * $img.W)
        $out   = New-Object System.Collections.Generic.List[double]
        for ($dj = -$rY; $dj -le $rY; $dj++) {
            $jj = $j0 + $dj; if ($jj -lt 0 -or $jj -ge $img.H) { continue }
            for ($di = -$rX; $di -le $rX; $di++) {
                $ii = ((($i0 + $di) % $img.W) + $img.W) % $img.W
                $out.Add($img.Rgba[($jj * $img.W + $ii) * 4] / 255.0 * $deformity)
            }
        }
        return $out
    }

    # One pad today; parsed as a list so adding a second does not need new code here.
    $lmTxt = (Get-Content $lmCfg -Raw) -replace '//.*'
    $pads  = [regex]::Matches($lmTxt,
        '(?s)FlattenArea\s*\{.*?latitude\s*=\s*(?<lat>-?[0-9.]+).*?longitude\s*=\s*(?<lon>-?[0-9.]+).*?flattenTo\s*=\s*(?<to>-?[0-9.]+).*?innerRadius\s*=\s*(?<ir>[0-9.]+).*?outerRadius\s*=\s*(?<orad>[0-9.]+)')

    if ($pads.Count -eq 0) {
        Warn 'Bennu_Landmarks.cfg has no parseable FlattenArea; pad check skipped'
    }
    foreach ($p in $pads) {
        $lat = [double]$p.Groups['lat'].Value
        $lon = [double]$p.Groups['lon'].Value
        $to  = [double]$p.Groups['to'].Value
        $ir  = [double]$p.Groups['ir'].Value
        $orad= [double]$p.Groups['orad'].Value

        $disc = Get-DiscHeights $himg $lat $lon $ir  $genDatum $genDef | Sort-Object
        $ring = Get-DiscHeights $himg $lat $lon $orad $genDatum $genDef | Sort-Object
        $dMin = $disc[0]; $dMax = $disc[$disc.Count - 1]
        $rMin = $ring[0]; $rMax = $ring[$ring.Count - 1]

        Write-Host ("  pad at {0} N {1} E: flattenTo {2} m, disc {3:N1}-{4:N1} m, ring {5:N1}-{6:N1} m" -f `
            $lat, $lon, $to, $dMin, $dMax, $rMin, $rMax)
        Write-Host ("    cut {0:N1} m / fill {1:N1} m" -f ($dMax - $to), ($to - $dMin)) -ForegroundColor DarkGray

        if ($to -lt $rMin -or $to -gt $rMax) {
            Fail ("pad flattenTo={0} is outside the {1:N1}-{2:N1} m relief of its own blend ring - it will read as a mesa or a pit" -f $to, $rMin, $rMax)
        } elseif ($to -lt $dMin -or $to -gt $dMax) {
            Warn ("pad flattenTo={0} is outside the flat disc's own {1:N1}-{2:N1} m range" -f $to, $dMin, $dMax)
        } else {
            Write-Host ("    OK   sits inside the local relief") -ForegroundColor DarkGray
        }

        # Steepest part of a cubic Hermite blend with zero end tangents is 1.5x average.
        $rimSlope = [math]::Atan(1.5 * [math]::Max($rMax - $to, $to - $rMin) / ($orad - $ir)) * 180 / [math]::PI
        if ($rimSlope -gt 30) {
            Fail ("blend rim reaches about {0:N0} deg - widen outerRadius" -f $rimSlope)
        } else {
            Write-Host ("    OK   blend rim peaks near {0:N0} deg" -f $rimSlope) -ForegroundColor DarkGray
        }
    }
} else {
    Warn 'landmark config, height map or derived values missing; pad check skipped'
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
#  8. Contracts
#
#  Contract Configurator resolves parameter, requirement and behaviour types by class
#  name at load time, and a name it does not recognise is a log line rather than a
#  crash - the contract simply never appears. So check every type name against the
#  classes in the installed assembly, and check the site the contracts point at is
#  still the site the terrain config prepares.
# -------------------------------------------------------------------------------------
Section '8. Contracts'

$ccCfg = Join-Path $cfgDir 'Compatibility\Bennu_Contracts.cfg'
$ccDll = Join-Path $GameData 'ContractConfigurator\ContractConfigurator.dll'

if (-not (Test-Path $ccCfg)) {
    Warn 'Bennu_Contracts.cfg not found'
} else {
    $ccTxt = (Get-Content $ccCfg -Raw) -replace '//.*'

    if (Test-Path $ccDll) {
        $ccNames = $null
        try {
            $ccAsm = [System.Reflection.Assembly]::LoadFrom($ccDll)
            $ccTypes = @()
            try { $ccTypes = $ccAsm.GetTypes() } catch [System.Reflection.ReflectionTypeLoadException] {
                $ccTypes = $_.Exception.Types | Where-Object { $_ -ne $null }
            }
            $ccNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
            foreach ($t in $ccTypes) {
                if (-not $t.Name) { continue }
                # CC strips the Factory / Requirement suffix to get the config type name.
                [void]$ccNames.Add($t.Name)
                [void]$ccNames.Add(($t.Name -replace 'Factory$', ''))
                [void]$ccNames.Add(($t.Name -replace 'Requirement$', ''))
            }
            Write-Host "  reflected $($ccTypes.Count) types from ContractConfigurator.dll" -ForegroundColor DarkGray
        } catch {
            Warn "could not reflect ContractConfigurator.dll ($($_.Exception.Message)); type check skipped"
        }

        if ($ccNames) {
            $used = [regex]::Matches($ccTxt, '(?m)^\s*type\s*=\s*(\S+)') |
                    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
            foreach ($u in $used) {
                if ($ccNames.Contains($u)) {
                    Write-Host ("  OK   type '{0}'" -f $u) -ForegroundColor DarkGray
                } else {
                    Fail "Bennu_Contracts.cfg uses type '$u', which is not a class in the installed ContractConfigurator"
                }
            }
        }
    } else {
        Warn 'ContractConfigurator not installed; contract type check skipped'
    }

    # Biomes named in contracts must exist on the body.
    $bTxt = Get-Content (Join-Path $cfgDir 'Configs\Bennu.cfg') -Raw
    foreach ($m in [regex]::Matches($ccTxt, '(?m)^\s*biome\s*=\s*(.+?)\s*$')) {
        $bn = $m.Groups[1].Value
        if ($bTxt -match [regex]::Escape("name = $bn")) {
            Write-Host ("  OK   biome '{0}'" -f $bn) -ForegroundColor DarkGray
        } else {
            Fail "contract targets biome '$bn' which is not declared in Bennu.cfg"
        }
    }

    # The waypoint has to sit on the pad. These live in two different files, so nothing
    # else would notice if one moved and the other did not.
    $wp = [regex]::Match($ccTxt, '(?s)WAYPOINT\s*\{.*?latitude\s*=\s*(?<lat>-?[0-9.]+).*?longitude\s*=\s*(?<lon>-?[0-9.]+)')
    if ($wp.Success -and (Test-Path $lmCfg)) {
        $lmTxt2 = (Get-Content $lmCfg -Raw) -replace '//.*'
        $pad = [regex]::Match($lmTxt2, '(?s)FlattenArea\s*\{.*?latitude\s*=\s*(?<lat>-?[0-9.]+).*?longitude\s*=\s*(?<lon>-?[0-9.]+)')
        if ($pad.Success) {
            $dLat = [math]::Abs([double]$wp.Groups['lat'].Value - [double]$pad.Groups['lat'].Value)
            $dLon = [math]::Abs([double]$wp.Groups['lon'].Value - [double]$pad.Groups['lon'].Value)
            if ($dLat -gt 0.01 -or $dLon -gt 0.01) {
                Fail ("contract waypoint ({0},{1}) does not sit on the prepared pad ({2},{3})" -f `
                    $wp.Groups['lat'].Value, $wp.Groups['lon'].Value, $pad.Groups['lat'].Value, $pad.Groups['lon'].Value)
            } else {
                Write-Host ("  OK   waypoint sits on the Nightingale pad") -ForegroundColor DarkGray
            }
        }
    }

    # A chained contract that names a contract which does not exist never unlocks.
    $defined = [regex]::Matches($ccTxt, '(?m)^\s*name\s*=\s*(\w+)\s*$') | ForEach-Object { $_.Groups[1].Value }
    foreach ($m in [regex]::Matches($ccTxt, '(?m)^\s*contractType\s*=\s*(\w+)')) {
        $ct = $m.Groups[1].Value
        if ($defined -contains $ct) {
            Write-Host ("  OK   chains from '{0}'" -f $ct) -ForegroundColor DarkGray
        } else {
            Fail "contract requires completion of '$ct', which is not defined in this pack"
        }
    }
}

# -------------------------------------------------------------------------------------
#  9. Scaled-space albedo source
#
#  In Parallax's FromTerrain scaled mode the TERRAIN detail textures are composited over
#  _ColorMap, weighted by the influence map. This pack borrows Gilly's detail set for a
#  body far darker than Gilly, so in that mode the borrowed rock supplies most of the
#  albedo and the body renders washed out - which is what "shiny in map view" turned out
#  to be. Nothing else in the pipeline notices, because every file involved is valid.
# -------------------------------------------------------------------------------------
Section '9. Scaled-space albedo'

$pxCfg = Join-Path $cfgDir 'Compatibility\Bennu_Parallax.cfg'
if ((Test-Path $pxCfg) -and ([System.Management.Automation.PSTypeName]'BennuGen.Preview').Type) {
    $pxTxt = (Get-Content $pxCfg -Raw) -replace '//.*'
    $mode = if ($pxTxt -match '(?m)^\s*mode\s*=\s*(\w+)') { $Matches[1] } else { $null }
    Write-Host "  scaled mode = $mode" -ForegroundColor DarkGray

    function Get-MeanLuma($path) {
        if (-not (Test-Path $path)) { return $null }
        try { $img = [BennuGen.Preview]::Load($path) } catch { return $null }
        $n = $img.W * $img.H
        $stride = [Math]::Max(1, [int]($n / 120000))
        [double]$sum = 0; $c = 0
        for ($i = 0; $i -lt $n; $i += $stride) {
            $o = $i * 4
            $sum += 0.299 * $img.Rgba[$o] + 0.587 * $img.Rgba[$o+1] + 0.114 * $img.Rgba[$o+2]
            $c++
        }
        return $sum / $c
    }

    $colorLuma = Get-MeanLuma (Join-Path $cfgDir 'PluginData\Bennu_Color.dds')
    if ($null -ne $colorLuma) {
        Write-Host ("  colour map mean luma {0:N1}/255 ({1:P0} albedo as authored)" -f $colorLuma, ($colorLuma/255)) -ForegroundColor DarkGray
    }

    if ($mode -eq 'FromTerrain') {
        $detailPath = if ($pxTxt -match '_MainTexMid\s*=\s*(\S+)') { Join-Path $GameData $Matches[1] } else { $null }
        $detailLuma = if ($detailPath) { Get-MeanLuma $detailPath } else { $null }
        if ($null -ne $detailLuma -and $null -ne $colorLuma) {
            $ratio = $detailLuma / $colorLuma
            Write-Host ("  detail texture mean luma {0:N1}/255, ratio to colour map {1:N2}x" -f $detailLuma, $ratio) -ForegroundColor DarkGray
            if ($ratio -gt 1.25) {
                Fail ("FromTerrain mode composites the detail texture over the colour map, and the detail set is {0:N2}x brighter - scaled space will render washed out. Use mode = Baked, or supply detail textures matching this body's albedo." -f $ratio)
            } else {
                Write-Host '  OK   detail texture brightness is close enough to the colour map' -ForegroundColor DarkGray
            }
        }
    } else {
        Write-Host '  OK   Baked mode - scaled space renders the colour map directly, no detail compositing' -ForegroundColor DarkGray
    }

    # Hapke flattens shading and removes limb darkening. Stock bodies run 0.30 (Gilly) to
    # 1.15 (Eve); anything darker than Gilly has no business above Gilly's value.
    $scaledHapke = if ($pxTxt -match '(?s)TerrainMaterialOverride.*?_Hapke\s*=\s*([0-9.]+)') { [double]$Matches[1] } else { $null }
    if ($null -ne $scaledHapke) {
        if ($scaledHapke -gt 0.30) {
            Warn ("scaled _Hapke is $scaledHapke; Gilly - the darkest stock body - ships 0.30, and above it the lit face flattens toward uniform")
        } else {
            Write-Host ("  OK   scaled _Hapke {0} is at or below Gilly's 0.30" -f $scaledHapke) -ForegroundColor DarkGray
        }
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

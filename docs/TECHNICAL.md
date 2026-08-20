# Technical notes

Everything about how the body is built, how it's checked, and what flight testing changed.
For the player-facing overview see the [README](../README.md).

---

## The generation pipeline

There is no hand-painted art here. The shape model and all four maps are generated
procedurally and deterministically.

```
Tools/
  BennuMapGen.cs            shape model, colour, normal and biome generation
  BennuIconGen.cs           the map-view node icon
  Dds.cs                    DDS writer (L8, RGBA32, DXT1, DXT5nm + mipmaps)
  DdsPreview.cs             independent DDS decoder + shaded globe renderer
  Generate-BennuMaps.ps1    run this to (re)build the maps
  Generate-BennuIcon.ps1    run this to (re)build the map icon
  Preview-BennuMaps.ps1     decode them back and render previews
  Validate-Bennu.ps1        static checks over the whole pack
```

Requires nothing but Windows PowerShell 5.1 and .NET Framework — the C# is compiled
in-session via `Add-Type`.

### The shape

A spinning-top base (conical flanks falling off as `|sin lat|^1.4` meeting a narrow
equatorial crest — a plain oblate spheroid looks far too round), plus fBm lumpiness, ridged
mid-scale facets, fine roughness, and a 260-crater population sampled from a bounded power
law with bowl, rim-crest and ejecta profiles. All noise is sampled in 3D on the sphere, so
there are no antimeridian seams and no polar pinching.

### Outputs

Into `Bennu/PluginData/`:

| File | Format | Used by |
|---|---|---|
| `Bennu_Height.dds` | L8 1024×512 | PQS `VertexHeightMap`, Parallax `_HeightMap` |
| `Bennu_Color.dds` | DXT1 + 13 mips | scaled space, PQS `VertexColorMap` |
| `Bennu_Normal.dds` | DXT5nm + 13 mips | scaled-space normals |
| `Bennu_Biome.dds` | RGBA32 uncompressed | biome map (uncompressed so colours stay exact) |
| `Bennu_Derived.cfg` | text | the radius/deformity the maps imply |

Formats were matched byte-for-byte against the files Parallax already ships — same
pixel-format flags, same DXT5nm normal-map hint bit (`0x80000004`), same L8 height layout.
All DDS output is written bottom-up, which is what KSP's loader expects.

### Why the height map is the lowest-resolution file here

Deliberately. The shape is evaluated at 4096×2048 and box-averaged down to 1024×512 before
quantising, because `VertexHeightMap` is 8-bit and texel size is the only lever on how
coarse that quantisation looks as geometry — see [the flight test notes](#what-the-first-flight-test-changed).
Averaging before quantising also low-passes away the detail the coarser grid can't carry,
which is what the continuous PQS noise mods then put back without any quantisation at all.

### On the colour map looking "too bright"

Bennu's 0.044 albedo renders to about 61/255 in sRGB, and that is what the texture is
calibrated to — it only looks light in the preview renders, which lift exposure the way
published mission imagery does. The physically correct 0.044 goes in Kopernicus's `albedo`
field, where it drives solar panel output and thermals.

---

## The map node icon

`Orbit { iconTexture }` replaces the icon KSP draws for the body in map view and the
tracking station. Kopernicus turns the whole texture into a sprite with a centred pivot
and hands it to `body.MapObject.uiNode.SetIcon`, so any square RGBA texture works — there
is no atlas or cell layout to match.

It is the one texture in the pack that does **not** live in `PluginData`. Kopernicus's
`Texture2DParser` tries `GameDatabase.ExistsTexture` first and only then falls back to
loading off disk, and KSP skips `PluginData` when building the GameDatabase. Putting the
icon in a normal folder keeps it on the path that is guaranteed to resolve, and it costs
16 KB.

**It is the one place in this pack where a number is chosen for legibility over fidelity,
and the source says so.** The literal silhouette is 1147 m at the equatorial crest against
996 m at the poles — 1.13:1, which at the ~20 px KSP actually draws a node icon at is a
circle, indistinguishable from every stock body. So the icon is drawn to the body's shape
*law* — conical flanks meeting an equatorial crest, using the body's own crest width — with
three proportions pushed until they survive at that size: polar ratio 0.52 rather than
0.87, flank exponent 1.0 rather than 1.4 (straight flanks instead of rounded), and a small
explicit crest. The 3D noise terms are not applied; they are sampled on the sphere and have
no meaning for a 2D outline.

Because the body is a solid of revolution whose radius depends only on latitude, the
edge-on silhouette *is* the axial cross-section, so the outline is exact for the shape it
is drawing rather than an approximation of a projection.

```bash
powershell -File Tools/Generate-BennuIcon.ps1
```

## Map view zoom

`Properties { maxZoom }` is the number of metres that fit across the screen at the closest
the camera will go. Kopernicus stores it on the body as `"maxZoom"` and its default is
`10 * 6000` = 60,000 m:

```csharp
[ParserTarget("maxZoom")]
public NumericParser<Single> MinDistance
{
    get { return Value.Get("maxZoom", 10 * 6000f); }
    set { Value.Set("maxZoom", value.Value / 6000f); }
}
```

That is fine for a planet and absurd for a body 2.3 km across — Bennu never grew past a few
percent of the screen and the camera stopped several kilometres out. It ships at 2,500 m,
which frames the whole body. The camera distance that implies is `2500 / (2 tan(fov/2))`,
roughly 2.2 km from the centre against a maximum surface radius of 1168 m, so there is
still most of a body radius of clearance. Below about 1400 it will start to clip.

## Why the body went flat and glossy as you zoomed out

The scaled-space material was running `_Hapke = 0.85` in `TerrainMaterialOverride`, *above*
the terrain material's 0.6, on the theory that an airless body should read flat from a
distance.

That reasoning was backwards. Hapke backscatter is precisely the term that removes limb
darkening, and Parallax's `ScaledShaderBank.cfg` notes that `Custom/ParallaxScaled` shares
properties with the terrain shader — so this value really is what lights the body in map
view. At 0.85 the terminator all but disappeared, leaving a uniformly bright disc that
reads as smooth and glossy even with `_SpecularIntensity` and `_EnvironmentMapFactor` both
at zero. The give-away was that it only happened as the body got small: near-field the PQS
terrain was lit properly, far-field the scaled material took over with a different shading
model.

Scaled now matches terrain at 0.6, so the shading model no longer changes with distance.
For calibration, Parallax gives Gilly — the closest stock analogue, and the texture set this
pack borrows — **0.30**, the lowest of any stock body, and none of Parallax's own scaled
configs override `_Hapke` at all.

`_PlanetBumpScale` also went 1.0 → 1.25 (matching Ike), because Bennu shrinks to a handful
of pixels faster than anything else in the system and the normal map's relief averages away
toward flat as it mips down.

### Regenerating

```bash
powershell -File Tools/Generate-BennuMaps.ps1
```

Tunables live in the `Cfg` class at the top of `BennuMapGen.cs` — scale factor, ridge
sharpness, noise amplitudes, crater population. Every seed is fixed, so a re-run reproduces
identical bytes. Afterwards, copy the new `radius` and `deformity` from
`PluginData/Bennu_Derived.cfg` into `Bennu.cfg` and re-run the validator.

Changing `Cfg.Scale` changes the 4× size factor; you'll want to revisit `geeASL` and
`timewarpAltitudeLimits` in `Bennu.cfg` to match.

---

## Validating

```bash
powershell -File Tools/Validate-Bennu.ps1
```

This is the stand-in for not being able to launch KSP, and it checks the failure modes that
otherwise surface as a silent nothing or a wall of exceptions in `KSP.log`:

1. Brace balance in every `.cfg`
2. Every key in `Bennu.cfg` checked against the `ParserTarget` names **reflected live out of
   your installed `Kopernicus.dll`** — catches typos and version drift rather than trusting
   a hardcoded list
3. Every referenced texture, model and mesh path resolves, in this pack or in GameData
4. All thirteen biome colours match `Bennu_Biome.dds` byte-for-byte, **in both directions**
   (no declared biome missing from the map, no colour in the map without a biome)
5. `radius`, `deformity`, `mapMaxHeight` and Parallax's `maxTerrainAltitude` all agree with
   what the generator actually produced
6. Derived physics printed for sanity, with hard failures if the SOI collapses toward the
   body radius or the top timewarp tier ends up outside the SOI
7. Every `BIOME_RESOURCE` targets a biome that actually exists; every resource name exists
   in stock or CRP; every resource has a SCANsat cutoff and vice versa; every `@Item[X]`
   SCANsat patch targets a node SCANsat really ships (these fail *silently* otherwise — no
   error, no cutoffs, map quietly on defaults); and the SCANsat altimetry range actually
   spans the terrain

It also reports the **terrain slope distribution** measured off the shipped height map, and
fails if 8-bit quantisation alone would produce facets steeper than 15°. That check exists
because that is exactly what made the first build unlandable, and nothing else in the
pipeline would have caught it.

Current state: **all checks pass.**

`Tools/Preview-BennuMaps.ps1` additionally decodes the shipped DDS files with an independent
decoder and renders shaded globes to `Tools/preview/` — if those come out right, the block
layout, header flags and orientation are right.

---

## What the first flight test changed

The first build flew, and four things were wrong. All are fixed; recording them because
three were caused by reasoning that looked sound and wasn't.

### Terrain was unlandable

The cause was not the noise settings — it was 8-bit height quantisation. `VertexHeightMap`
is greyscale-only (Kopernicus parses it as `MapSOParserGreyScale`; KSP's `MapSO` does
support a 16-bit `HeightAlpha` depth, but `VertexHeightMap` can't use it). With 226 m of
relief that fixes the vertical step at 0.89 m, and at 4096×2048 the texels were 1.5 m apart
— so a single quantisation level between neighbours was a **30° facet**, as real geometry,
because MapSO's bilinear filter ramps smoothly between texel centres.

Raising resolution makes this worse, not better. The height map now ships at **1024×512**
(6.2 m texels, 7.9° worst case, versus the ~11° Gilly ships with); the normal and colour
maps stay at 4096×2048 since they only affect shading.

A second contributor was the `VertexSimplexHeight` mod, which at deformity 5 / frequency 250
was putting 5 m of relief at 7.6 m wavelength — a 65% slope.

Measured over the whole body:

| | median | >15° | >30° |
|---|---|---|---|
| first build | 15.9° | 65.2% | 12.2% |
| **now** | **9.7°** | **24.9%** | **4.7%** |
| Gilly (for scale) | 16.4° | 55.4% | 6.8% |
| Bop (for scale) | 8.5° | 12.7% | 0.0% |

### Scatters were absurdly dense and brown

Density is applied per PQS quad, and quad size scales with body radius — Bennu's quads are
~11.6 m across against Gilly's ~80 m, so an identical `populationMultiplier` puts ~47× more
rock per square metre here. The first build then used multipliers *above* Gilly's on top of
that. Now anchored to Pol's approach: hold `populationMultiplier` at 1 and tune
`spawnChance`.

The brown was arithmetic — Gilly's rock atlas averages RGB (93, 78, 69), a warm tan, and
`_Color` multiplies it, so a neutral grey multiplier keeps the brown and merely darkens it.
The multipliers are now the inverse of that bias.

### Glossy from the tracking station

`_SpecularIntensity`, `_EnvironmentMapFactor` and `_FresnelPower` all contributed, and
fresnel dominates at distance because most of the visible disc is at a grazing angle. All
near zero now, with `_Hapke` raised in scaled space for the flat, dusty, un-shaded look an
airless body should have. Deferred's screen-space reflections key off specular, which is why
these are at zero rather than merely low.

### Two harmless log warnings

`Bennu_EVE.cfg` and `Bennu_Deferred.cfg` were comment-only, so KSP logged "Cannot create
config from file" for each. They're `.txt` now.

### A correction

An earlier version of the documentation claimed the SCANsat altimetry entry was necessary to
stop the map rendering flat. It isn't — SCANsat builds its terrain list from the savegame and
auto-generates a config for unknown bodies, deriving the range from the body itself (it
logged `Max Height: [200m]` against a real 219 m). The entry is kept as a refinement, not a
fix. The `cannot be found in master terrain storage list` line it logs is also not a Bennu
problem — stock bodies including Laythe log it identically.

---

## Compatibility patch reasoning

### Parallax Continued — `Bennu_Parallax.cfg`, `Bennu_Parallax_Scatters.cfg`

Handles the two things Parallax needs on every body: the scaled-mesh swap and the near-field
`Parallax` PQS subdivision mod. Everything is gated on `Parallax_StockTerrainTextures` /
`Parallax_StockScatterTextures` being present, so the paths always resolve.

`cacheFile` is redirected to `ParallaxContinued/Models/ScaledMesh.bin`, because Parallax
displaces a dense uniform sphere from the height map rather than using a PQS-baked mesh.

### KerbalFX — `Bennu_KerbalFX.cfg`

The important bit is `override_biome = true`. KerbalFX tints dust by sampling the body's
biome map, which assumes the biome map is painted in surface colours. Bennu's isn't — its
biome colours are deliberately distinct markers so Kopernicus can tell thirteen biomes apart
by exact colour match, and Benben Saxum is bright orange. Left alone, KerbalFX would throw up
orange dust there.

AeroFX is atmospheric-only and BlastFX is configured globally; neither needs an entry.

### SCANsat — `Bennu_SCANsat.cfg`

Keyed by name, not index: the lookup is `SCANcontroller.getTerrainNode(string name)`, which
is why a hardcoded `index` is safe here even alongside other planet packs. Entries are
deleted before being added, because SCANsat rewrites `SCANcolors.cfg` when you change colours
in its UI and a saved entry plus this patch would otherwise leave two.

Stock's 2.5/14 resource cutoff defaults would saturate solid for Hydrates (which reaches 50)
and stay invisible for Uraninite (which peaks at 1.2), hence the per-resource cutoffs.

### Resources — `Bennu_Resources.cfg`

Without this, Bennu would inherit only the stock global Ore distribution and every CRP
resource map would come back empty.

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
  Dds.cs                    DDS writer (L8, RGBA32, DXT1, DXT5nm + mipmaps)
  DdsPreview.cs             independent DDS decoder + shaded globe renderer
  Generate-BennuMaps.ps1    run this to (re)build the maps
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

Into `GameData/Bennu/PluginData/`:

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

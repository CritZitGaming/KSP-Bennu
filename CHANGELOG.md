# Changelog

## 1.0.0

First public release.

- 101955 Bennu as a Kopernicus `CelestialBody` — landable terrain with colliders, thirteen
  biomes with their own science text, and a real orbit crossing Kerbin's.
- Procedurally generated height, colour, normal and biome maps, reproducible byte-for-byte
  from `Tools/Generate-BennuMaps.ps1`.
- Compatibility patches for Parallax Continued, PlanetShine, KerbalFX and SCANsat, plus a
  full stock + CRP resource distribution with per-biome overrides. All gated behind
  `:NEEDS[…]`.
- Documented no-ops for EVE and Deferred, explaining why neither needs a patch.
- `Tools/Validate-Bennu.ps1` static validator over the whole pack.

Fixes applied after the first flight test — unlandable terrain from 8-bit height
quantisation, over-dense brown scatters, and specular gloss in the tracking station — are
detailed in [docs/TECHNICAL.md](docs/TECHNICAL.md).

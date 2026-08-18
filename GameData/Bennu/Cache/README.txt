This folder holds the scaled-space mesh Kopernicus bakes from Bennu's PQS on first
load (Bennu.bin). It is generated at runtime - there is nothing to install here.

This placeholder exists only so the empty folder survives being zipped or copied.

Note: when Parallax Continued is installed, Compatibility/Bennu_Parallax.cfg redirects
cacheFile to ParallaxContinued/Models/ScaledMesh.bin instead, because Parallax displaces
a dense uniform sphere from the height map rather than using a PQS-baked mesh. In that
case this folder stays empty, which is expected.

If Bennu ever looks wrong in map view or the tracking station after you change the
height map or the radius, delete Bennu.bin from here and let it rebuild.

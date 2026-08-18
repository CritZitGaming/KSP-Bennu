// =====================================================================================
//  BennuMapGen  --  procedural shape-model / texture generator for the KSP Bennu body
// =====================================================================================
//
//  Produces, into GameData/Bennu/PluginData/:
//
//     Bennu_Height.dds   L8  4096x2048   PQS VertexHeightMap + Parallax scaled _HeightMap
//     Bennu_Color.dds    DXT1 4096x2048  scaled-space albedo (+ mipmaps)
//     Bennu_Normal.dds   DXT5nm 4096x2048 scaled-space normals (+ mipmaps)
//     Bennu_Biome.dds    RGBA32 1024x512 biome map (uncompressed - exact colours)
//     Bennu_Biome.png    identical, as a fallback if the DDS loader misbehaves
//     Bennu_Derived.cfg  the exact radius/deformity/altitude numbers the maps imply
//
//  DDS ORIENTATION NOTE
//  --------------------
//  KSP feeds DDS bytes straight into Unity's Texture2D, whose origin is bottom-left,
//  while DDS stores scanlines top-down. Every DDS KSP ships is therefore authored
//  vertically flipped. This generator builds all rasters with row 0 = SOUTH pole and
//  writes them in that order, which lands north-up in game and matches PQS, whose
//  normalised `vertex.latitude` also runs 0 = south -> 1 = north.
//  The PNG path is written top-down (north first) because Unity's PNG decoder flips
//  for you; both files therefore describe the same globe.
//
//  LONGITUDE: u = 0 -> 180 W, u = 0.5 -> 0, u = 1 -> 180 E (the KSP planet-map norm).
//
//  All randomness is seeded, so re-running reproduces byte-identical maps.
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace BennuGen
{
    // ---------------------------------------------------------------------------------
    //  Tunables. Everything the body's look depends on lives here.
    // ---------------------------------------------------------------------------------
    public static class Cfg
    {
        // Real 101955 Bennu, multiplied up so it is actually flyable in KSP.
        public const double Scale = 4.0;

        // Real Bennu radii (m): equatorial ridge crest vs. polar.
        public const double RealEquatorial = 282.0;
        public const double RealPolar = 249.0;
        public const double RealMean = 244.9;

        public static double REquator { get { return RealEquatorial * Scale; } } // 1128
        public static double RPolar { get { return RealPolar * Scale; } }        //  996

        // Raster sizes.
        //
        // HeightW/H is the resolution the shape is EVALUATED at. HeightOutW/H is what
        // actually ships as Bennu_Height.dds, box-averaged down from it.
        //
        // WHY THE HEIGHT MAP IS DELIBERATELY LOW RESOLUTION
        // -------------------------------------------------
        // Kopernicus' VertexHeightMap is 8-bit only - its parser is MapSOParserGreyScale,
        // and although KSP's MapSO does support a 16-bit HeightAlpha depth, VertexHeightMap
        // cannot use it. So the vertical quantisation step is fixed at deformity/255,
        // here about 0.89 m.
        //
        // That step is what made the first test build unlandable. At 4096 wide the
        // texels are 1.5 m apart, so a single quantisation level between neighbouring
        // texels is a 30 degree facet - and MapSO's bilinear filter ramps smoothly
        // between texel centres, so those facets are real geometry, not just shading.
        // Raising the resolution makes this WORSE, not better: smaller texels, same step.
        //
        // The fix is to make texels large relative to the step. At 1024 wide the spacing
        // is about 6 m and the worst-case facet drops to 8.4 degrees. For calibration,
        // Gilly ships its height map at 1024x512 too, where the same arithmetic gives
        // roughly 11 degrees - so this is gentler than a stock body that is known to be
        // landable. Fine detail is then supplied by the continuous PQS noise mods in
        // Bennu.cfg, which have no quantisation at all.
        //
        // The normal and colour maps stay at full resolution - they only affect shading,
        // never collision, so there is no reason to coarsen them.
        public const int HeightW = 4096, HeightH = 2048;
        public const int HeightOutW = 1024, HeightOutH = 512;
        public const int ColorW = 4096, ColorH = 2048;
        public const int BiomeW = 1024, BiomeH = 512;

        // Equatorial ridge. Bennu's silhouette is a "spinning top": conical flanks
        // meeting a narrow raised band at the equator. `RidgeConeExp` controls how
        // conical the flanks are (1.0 = pure diamond, 2.0 = nearly an ellipsoid) and
        // `RidgeBump` adds the distinct crest line on top of that.
        public const double RidgeConeExp = 1.40;
        public const double RidgeConeWeight = 0.68;  // cone vs. plain oblateness
        public const double RidgeBump = 19.0;        // metres of extra crest
        public const double RidgeBumpSigmaDeg = 9.0;

        // Shape noise, in metres at the working (already scaled) size.
        // PERSISTENCE IS THE SLOPE KNOB. With lacunarity 2, a persistence of 0.5 means
        // every octave halves in amplitude while doubling in frequency - so every
        // octave contributes the SAME slope, and they stack. Dropping persistence below
        // 0.5 makes each finer octave contribute less slope than the one before, which
        // is what keeps the surface walkable while retaining large-scale shape.
        public const double LumpAmplitude = 34.0;  // broad, silhouette-defining lumpiness
        public const double LumpFrequency = 1.9;
        public const int LumpOctaves = 5;
        public const double LumpPersistence = 0.42;

        // Mid-scale ridged noise gives the angular, faceted look of a rubble pile
        // rather than the smooth blobbiness plain fBm produces.
        //
        // These were all roughly halved after the first flight test: at the original
        // amplitudes the surface was too broken to put a lander on. Slope, not height,
        // is what makes terrain unlandable - and slope is amplitude divided by
        // wavelength, so on a body only 6 km around even a few metres of relief at
        // short wavelength becomes a cliff.
        public const double FacetAmplitude = 5.0;
        public const double FacetFrequency = 5.0;
        public const int FacetOctaves = 4;
        public const double FacetPersistence = 0.42;

        public const double RoughAmplitude = 2.2;  // metre-scale surface roughness
        public const double RoughFrequency = 14.0;
        public const int RoughOctaves = 4;
        public const double RoughPersistence = 0.45;

        public const double MicroAmplitude = 0.8;  // sub-texel relief
        public const double MicroFrequency = 110.0;
        public const int MicroOctaves = 3;
        public const double MicroPersistence = 0.45;

        // Crater population (diameters in metres, already scaled).
        public const int CraterCount = 260;
        public const double CraterMinD = 38.0;
        public const double CraterMaxD = 640.0;
        public const double CraterPowerLaw = 2.1;   // N(>D) proportional to D^-a
        public const double CraterDepthRatio = 0.105;

        public const int SeedShape = 101955;
        public const int SeedCraters = 19990911; // Bennu's discovery date
        public const int SeedColor = 20201020;   // the TAG sample collection date
    }

    // ---------------------------------------------------------------------------------
    //  Perlin gradient noise, sampled in 3D on the sphere so there are no seams at the
    //  antimeridian and no pinching at the poles (which 2D lat/lon noise both suffer).
    // ---------------------------------------------------------------------------------
    public sealed class Perlin
    {
        private readonly int[] p = new int[512];

        public Perlin(int seed)
        {
            int[] perm = new int[256];
            for (int i = 0; i < 256; i++) perm[i] = i;
            Random rng = new Random(seed);
            for (int i = 255; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                int t = perm[i]; perm[i] = perm[j]; perm[j] = t;
            }
            for (int i = 0; i < 512; i++) p[i] = perm[i & 255];
        }

        private static double Fade(double t) { return t * t * t * (t * (t * 6 - 15) + 10); }
        private static double Lerp(double a, double b, double t) { return a + t * (b - a); }

        private static double Grad(int hash, double x, double y, double z)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        public double Noise(double x, double y, double z)
        {
            int X = (int)Math.Floor(x) & 255;
            int Y = (int)Math.Floor(y) & 255;
            int Z = (int)Math.Floor(z) & 255;
            x -= Math.Floor(x); y -= Math.Floor(y); z -= Math.Floor(z);
            double u = Fade(x), v = Fade(y), w = Fade(z);

            int A = p[X] + Y, AA = p[A] + Z, AB = p[A + 1] + Z;
            int B = p[X + 1] + Y, BA = p[B] + Z, BB = p[B + 1] + Z;

            return Lerp(
                Lerp(Lerp(Grad(p[AA], x, y, z), Grad(p[BA], x - 1, y, z), u),
                     Lerp(Grad(p[AB], x, y - 1, z), Grad(p[BB], x - 1, y - 1, z), u), v),
                Lerp(Lerp(Grad(p[AA + 1], x, y, z - 1), Grad(p[BA + 1], x - 1, y, z - 1), u),
                     Lerp(Grad(p[AB + 1], x, y - 1, z - 1), Grad(p[BB + 1], x - 1, y - 1, z - 1), u), v),
                w);
        }

        /// <summary>Fractal Brownian motion: layered noise, each octave finer and fainter.</summary>
        public double Fbm(double x, double y, double z, int octaves, double freq, double persistence)
        {
            double sum = 0, amp = 1, max = 0, f = freq;
            for (int i = 0; i < octaves; i++)
            {
                sum += Noise(x * f, y * f, z * f) * amp;
                max += amp;
                amp *= persistence;
                f *= 2.0;
            }
            return max > 0 ? sum / max : 0;
        }

        /// <summary>Ridged multifractal - gives the sharp crests real rubble piles show.</summary>
        public double Ridged(double x, double y, double z, int octaves, double freq, double persistence)
        {
            double sum = 0, amp = 1, max = 0, f = freq;
            for (int i = 0; i < octaves; i++)
            {
                double n = 1.0 - Math.Abs(Noise(x * f, y * f, z * f));
                sum += n * n * amp;
                max += amp;
                amp *= persistence;
                f *= 2.0;
            }
            return max > 0 ? (sum / max) * 2.0 - 1.0 : 0;
        }
    }

    // ---------------------------------------------------------------------------------
    public struct Crater
    {
        public double X, Y, Z;      // unit-vector centre
        public double AngRadius;    // angular radius, radians
        public double Depth;        // metres
        public double RimHeight;    // metres
        public double Freshness;    // 0 = ancient/buried, 1 = crisp bright rim
    }

    // ---------------------------------------------------------------------------------
    //  A named surface feature, used to paint the biome map. Coordinates are the real
    //  IAU / OSIRIS-REx locations where published.
    // ---------------------------------------------------------------------------------
    public sealed class Feature
    {
        public string Name;
        public string Display;
        public double Lat, Lon;      // degrees
        public double AngRadiusDeg;  // painted footprint
        public Color Col;
        public Feature(string n, string d, double lat, double lon, double r, int cr, int cg, int cb)
        { Name = n; Display = d; Lat = lat; Lon = lon; AngRadiusDeg = r; Col = Color.FromArgb(255, cr, cg, cb); }
    }

    // =================================================================================
    public static class Generator
    {
        static Perlin nShape, nRough, nMicro, nColorA, nColorB;
        static Crater[] craters;
        static double rMin, rMax;

        // -----------------------------------------------------------------------------
        public static void Run(string outDir)
        {
            Directory.CreateDirectory(outDir);

            nShape = new Perlin(Cfg.SeedShape);
            nRough = new Perlin(Cfg.SeedShape + 7);
            nMicro = new Perlin(Cfg.SeedShape + 13);
            nColorA = new Perlin(Cfg.SeedColor);
            nColorB = new Perlin(Cfg.SeedColor + 5);

            Log("Building crater population...");
            craters = BuildCraters();
            Log("  " + craters.Length + " craters, largest " +
                (2 * craters[0].AngRadius * Cfg.RealMean * Cfg.Scale).ToString("F0") + " m across");

            // ---- radius field -------------------------------------------------------
            Log("Evaluating shape model (" + Cfg.HeightW + "x" + Cfg.HeightH + ")...");
            double[] radius = new double[Cfg.HeightW * Cfg.HeightH];
            Parallel.For(0, Cfg.HeightH, j =>
            {
                for (int i = 0; i < Cfg.HeightW; i++)
                {
                    double lon, lat;
                    UV(i, j, Cfg.HeightW, Cfg.HeightH, out lon, out lat);
                    double x, y, z;
                    ToVec(lon, lat, out x, out y, out z);
                    radius[j * Cfg.HeightW + i] = SurfaceRadius(x, y, z);
                }
            });

            rMin = double.MaxValue; rMax = double.MinValue;
            for (int k = 0; k < radius.Length; k++)
            {
                if (radius[k] < rMin) rMin = radius[k];
                if (radius[k] > rMax) rMax = radius[k];
            }

            // Datum + deformity chosen so the 8-bit ramp spans exactly the real range.
            double datum = Math.Floor(rMin);
            double deformity = Math.Ceiling(rMax - datum);
            Log(string.Format(CultureInfo.InvariantCulture,
                "  radius {0:F1} .. {1:F1} m  ->  datum {2:F0}, deformity {3:F0}",
                rMin, rMax, datum, deformity));

            // ---- height map ---------------------------------------------------------
            // Box-average down to the shipping resolution BEFORE quantising. Averaging
            // first also low-passes away the detail the coarser grid could not carry,
            // which is exactly what we want - that content comes back as continuous
            // PQS noise instead of as quantised steps. See the note on Cfg.HeightOutW.
            Log(string.Format("Writing Bennu_Height.dds ({0}x{1}, from {2}x{3}) ...",
                Cfg.HeightOutW, Cfg.HeightOutH, Cfg.HeightW, Cfg.HeightH));

            int fx = Cfg.HeightW / Cfg.HeightOutW, fy = Cfg.HeightH / Cfg.HeightOutH;
            byte[] h8 = new byte[Cfg.HeightOutW * Cfg.HeightOutH];
            for (int j = 0; j < Cfg.HeightOutH; j++)
            {
                for (int i = 0; i < Cfg.HeightOutW; i++)
                {
                    double sum = 0;
                    for (int b = 0; b < fy; b++)
                        for (int a = 0; a < fx; a++)
                            sum += radius[(j * fy + b) * Cfg.HeightW + (i * fx + a)];
                    double t = (sum / (fx * fy) - datum) / deformity;
                    h8[j * Cfg.HeightOutW + i] = (byte)Clamp((int)Math.Round(t * 255.0), 0, 255);
                }
            }
            Dds.WriteL8(Path.Combine(outDir, "Bennu_Height.dds"), h8, Cfg.HeightOutW, Cfg.HeightOutH);

            // ---- normal map ---------------------------------------------------------
            Log("Deriving Bennu_Normal.dds (DXT5nm) ...");
            byte[] nrm = BuildNormalMap(radius, Cfg.HeightW, Cfg.HeightH);
            Dds.WriteDxt5(Path.Combine(outDir, "Bennu_Normal.dds"), nrm, Cfg.HeightW, Cfg.HeightH, true);

            // ---- colour map ---------------------------------------------------------
            Log("Painting Bennu_Color.dds (DXT1) ...");
            byte[] col = BuildColorMap(radius, Cfg.HeightW, Cfg.HeightH, datum, deformity);
            Dds.WriteDxt1(Path.Combine(outDir, "Bennu_Color.dds"), col, Cfg.ColorW, Cfg.ColorH);

            // ---- biome map ----------------------------------------------------------
            Log("Painting biome map ...");
            Feature[] feats = Features();
            byte[] biome = BuildBiomeMap(feats);
            Dds.WriteRgba32(Path.Combine(outDir, "Bennu_Biome.dds"), biome, Cfg.BiomeW, Cfg.BiomeH);
            WritePngFlipped(Path.Combine(outDir, "Bennu_Biome.png"), biome, Cfg.BiomeW, Cfg.BiomeH);

            // ---- derived numbers for the Kopernicus config --------------------------
            WriteDerived(Path.Combine(outDir, "Bennu_Derived.cfg"), datum, deformity, feats);

            Log("Done.");
        }

        // -----------------------------------------------------------------------------
        //  Texel -> (lon, lat) in radians. Row 0 is the south pole; u = 0 is 180 W.
        // -----------------------------------------------------------------------------
        static void UV(int i, int j, int w, int h, out double lon, out double lat)
        {
            double u = (i + 0.5) / w;
            double v = (j + 0.5) / h;
            lon = (u - 0.5) * 2.0 * Math.PI;
            lat = (v - 0.5) * Math.PI;
        }

        static void ToVec(double lon, double lat, out double x, out double y, out double z)
        {
            double cl = Math.Cos(lat);
            x = cl * Math.Cos(lon);
            y = cl * Math.Sin(lon);
            z = Math.Sin(lat);   // +z = north = spin axis
        }

        // -----------------------------------------------------------------------------
        //  THE SHAPE. Returns surface radius in metres for a unit direction.
        // -----------------------------------------------------------------------------
        static double SurfaceRadius(double x, double y, double z)
        {
            // 1. Spinning-top base. Bennu is not an oblate spheroid: centrifugal
            //    reshaping of a rubble pile drives material equatorward, leaving
            //    near-conical flanks that meet in a narrow ridge. Falling off as
            //    |sin(lat)|^1.4 gives those straight-ish flanks (a pure ellipsoid
            //    would use cos^2 and look far too round), and the exponent staying
            //    above 1 keeps the crest itself rounded rather than a knife edge.
            double s = Math.Abs(z);                            // |sin(latitude)|
            double c2 = Math.Max(0.0, 1.0 - z * z);            // cos^2(latitude)
            double cone = 1.0 - Math.Pow(s, Cfg.RidgeConeExp);
            double shape = Cfg.RidgeConeWeight * cone + (1.0 - Cfg.RidgeConeWeight) * c2;
            double r = Cfg.RPolar + (Cfg.REquator - Cfg.RPolar) * shape;

            // 2. The crest line itself - a tight band right at the equator.
            double latDeg = Math.Asin(Clamp(z, -1.0, 1.0)) * 180.0 / Math.PI;
            double g = latDeg / Cfg.RidgeBumpSigmaDeg;
            r += Cfg.RidgeBump * Math.Exp(-g * g);

            // 3. Broad lumpiness - the irregular silhouette.
            r += Cfg.LumpAmplitude * nShape.Fbm(x, y, z, Cfg.LumpOctaves, Cfg.LumpFrequency, Cfg.LumpPersistence);

            // 4. Ridged mid-scale noise - angular facets and scarps.
            r += Cfg.FacetAmplitude * nShape.Ridged(x, y, z, Cfg.FacetOctaves, Cfg.FacetFrequency, Cfg.FacetPersistence);

            // 5. Fine roughness.
            r += Cfg.RoughAmplitude * nRough.Ridged(x, y, z, Cfg.RoughOctaves, Cfg.RoughFrequency, Cfg.RoughPersistence);

            // 6. Micro detail.
            r += Cfg.MicroAmplitude * nMicro.Fbm(x, y, z, Cfg.MicroOctaves, Cfg.MicroFrequency, Cfg.MicroPersistence);

            // 7. Craters.
            r += CraterHeight(x, y, z);

            return r;
        }

        // -----------------------------------------------------------------------------
        static Crater[] BuildCraters()
        {
            Random rng = new Random(Cfg.SeedCraters);
            List<Crater> list = new List<Crater>();
            double meanR = Cfg.RealMean * Cfg.Scale;

            for (int i = 0; i < Cfg.CraterCount; i++)
            {
                // Inverse-transform sample of a bounded power law: small craters are
                // overwhelmingly the most common, as on the real body.
                double u = rng.NextDouble();
                double a = 1.0 - Cfg.CraterPowerLaw;
                double dmin = Math.Pow(Cfg.CraterMinD, a);
                double dmax = Math.Pow(Cfg.CraterMaxD, a);
                double d = Math.Pow(dmin + u * (dmax - dmin), 1.0 / a);

                // Uniform on the sphere.
                double zz = rng.NextDouble() * 2.0 - 1.0;
                double ph = rng.NextDouble() * 2.0 * Math.PI;
                double sr = Math.Sqrt(Math.Max(0.0, 1.0 - zz * zz));

                Crater cr = new Crater();
                cr.X = sr * Math.Cos(ph);
                cr.Y = sr * Math.Sin(ph);
                cr.Z = zz;
                cr.AngRadius = (d * 0.5) / meanR;
                cr.Depth = d * Cfg.CraterDepthRatio * (0.7 + 0.6 * rng.NextDouble());
                cr.RimHeight = cr.Depth * (0.16 + 0.14 * rng.NextDouble());
                cr.Freshness = rng.NextDouble();
                list.Add(cr);
            }
            // Largest first, so later craters overprint earlier ones plausibly.
            list.Sort(delegate (Crater p, Crater q) { return q.AngRadius.CompareTo(p.AngRadius); });
            return list.ToArray();
        }

        static double CraterHeight(double x, double y, double z)
        {
            double sum = 0;
            for (int i = 0; i < craters.Length; i++)
            {
                Crater c = craters[i];
                double dot = x * c.X + y * c.Y + z * c.Z;
                // Cheap reject before the expensive acos. Generous enough to cover the
                // widened radius the irregularity term can produce.
                double outer = Math.Min(Math.PI, c.AngRadius * 1.85);
                if (dot < Math.Cos(outer)) continue;

                // Craters on a rubble pile are not circular; wobble the effective
                // radius with noise keyed to this crater so outlines look excavated
                // rather than stamped.
                double wob = 1.0 + 0.20 * nRough.Fbm(x * 7.0 + i * 3.1, y * 7.0 - i * 1.7, z * 7.0 + i * 2.3, 2, 1.0, 0.5);
                double rad = c.AngRadius * wob;

                double ang = Math.Acos(Clamp(dot, -1.0, 1.0));
                double t = ang / rad;

                if (t < 1.0)
                {
                    // Bowl: cosine falloff, exponent below 1 flattening the floor the
                    // way shallow, degraded craters on Bennu actually look.
                    double b = Math.Cos(t * Math.PI * 0.5);
                    double bowl = -c.Depth * Math.Pow(b, 0.8);
                    // Rim crest builds over the outer quarter and peaks exactly at t=1.
                    double rim = c.RimHeight * SmoothStep(0.72, 1.0, t);
                    sum += bowl + rim;
                }
                else if (t < 1.5)
                {
                    // Ejecta blanket decaying outside the rim.
                    double f = 1.0 - (t - 1.0) / 0.5;
                    sum += c.RimHeight * f * f;
                }
            }
            return sum;
        }

        // -----------------------------------------------------------------------------
        //  Tangent-space normals from the radius field, with the cos(lat) metric so the
        //  poles do not smear. Packed DXT5nm: X in alpha, Y in green.
        // -----------------------------------------------------------------------------
        static byte[] BuildNormalMap(double[] radius, int w, int h)
        {
            byte[] rgba = new byte[w * h * 4];
            double meanR = Cfg.RealMean * Cfg.Scale;
            double dLat = Math.PI / h;

            // Metres between vertically adjacent texels; used as the floor for the
            // horizontal spacing so the slope cannot blow up where meridians converge.
            double dyPhys = meanR * dLat;

            Parallel.For(0, h, j =>
            {
                double lat = ((j + 0.5) / h - 0.5) * Math.PI;
                double dLon = 2.0 * Math.PI / w;
                // True east-west texel spacing shrinks as cos(lat) and reaches zero at
                // the poles. Dividing by it unclamped turns sub-metre height wobble
                // into vertical cliffs and produces the classic pinwheel artefact at
                // the pole, so clamp it to a fraction of the north-south spacing.
                double dxPhys = Math.Max(meanR * Math.Cos(lat) * dLon, 0.35 * dyPhys);

                for (int i = 0; i < w; i++)
                {
                    int im = (i - 1 + w) % w, ip = (i + 1) % w;
                    int jm = Math.Max(0, j - 1), jp = Math.Min(h - 1, j + 1);

                    double dRdLon = (radius[j * w + ip] - radius[j * w + im]) / 2.0;
                    double dRdLat = (radius[jp * w + i] - radius[jm * w + i]) / (jp - jm);

                    // Surface slope components in metres per metre.
                    double sx = dRdLon / dxPhys;
                    double sy = dRdLat / dyPhys;

                    double nx = -sx, ny = -sy, nz = 1.0;
                    double inv = 1.0 / Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    nx *= inv; ny *= inv; nz *= inv;

                    int o = (j * w + i) * 4;
                    byte bx = (byte)Clamp((int)Math.Round((nx * 0.5 + 0.5) * 255.0), 0, 255);
                    byte by = (byte)Clamp((int)Math.Round((ny * 0.5 + 0.5) * 255.0), 0, 255);
                    rgba[o + 0] = 0;    // R unused by DXT5nm
                    rgba[o + 1] = by;   // G = Y
                    rgba[o + 2] = 0;    // B unused
                    rgba[o + 3] = bx;   // A = X
                }
            });
            return rgba;
        }

        // -----------------------------------------------------------------------------
        //  Colour. Bennu's geometric albedo is 0.044 - literally darker than charcoal.
        //  Rendered at true reflectance it is an unreadable black blob, so the texture
        //  sits at a legible dark grey and the physically correct 0.044 goes in the
        //  Kopernicus `albedo` field instead, where it drives science and thermals.
        //  Hue variation follows the real body: bluer/brighter on fresh rugged boulder
        //  terrain, redder/darker on smooth ponded regolith.
        // -----------------------------------------------------------------------------
        static byte[] BuildColorMap(double[] radius, int w, int h, double datum, double deformity)
        {
            byte[] rgba = new byte[w * h * 4];
            double meanR = Cfg.RealMean * Cfg.Scale;
            double dLat = Math.PI / h;

            double dyPhys = meanR * dLat;

            Parallel.For(0, h, j =>
            {
                double lat = ((j + 0.5) / h - 0.5) * Math.PI;
                double dLon = 2.0 * Math.PI / w;
                // Same polar clamp as the normal map - see BuildNormalMap.
                double dxPhys = Math.Max(meanR * Math.Cos(lat) * dLon, 0.35 * dyPhys);

                for (int i = 0; i < w; i++)
                {
                    double lon = ((i + 0.5) / w - 0.5) * 2.0 * Math.PI;
                    double x, y, z; ToVec(lon, lat, out x, out y, out z);

                    int im = (i - 1 + w) % w, ip = (i + 1) % w;
                    int jm = Math.Max(0, j - 1), jp = Math.Min(h - 1, j + 1);
                    double dRdLon = (radius[j * w + ip] - radius[j * w + im]) / 2.0;
                    double dRdLat = (radius[jp * w + i] - radius[jm * w + i]) / (jp - jm);
                    double gx = dRdLon / dxPhys, gy = dRdLat / dyPhys;
                    double slope = Math.Sqrt(gx * gx + gy * gy);

                    // Base very dark carbonaceous grey.
                    double v = 0.150;

                    // Two independent noise fields: terrain unit, and fine mottling.
                    double unit = nColorA.Fbm(x, y, z, 4, 2.6, 0.55);      // -1..1
                    double mott = nColorB.Fbm(x, y, z, 5, 26.0, 0.5);

                    v += 0.030 * unit;
                    v += 0.022 * mott;

                    // Steep faces expose fresher, brighter material.
                    v += 0.055 * Math.Min(1.0, slope * 1.6);

                    // Crests catch light; hollows collect dark fines.
                    double relief = (radius[j * w + i] - datum) / deformity;
                    v += 0.020 * (relief - 0.5);

                    v = Clamp(v, 0.075, 0.34);

                    // Blue on rugged/fresh, red on smooth/old.
                    double blueness = Clamp(0.5 + 0.9 * (Math.Min(1.0, slope * 1.6) - 0.35) + 0.35 * unit, 0.0, 1.0);
                    double rF = 1.045 - 0.075 * blueness;
                    double gF = 1.000;
                    double bF = 0.945 + 0.105 * blueness;

                    int o = (j * w + i) * 4;
                    rgba[o + 0] = (byte)Clamp((int)Math.Round(Math.Sqrt(v) * rF * 255.0 * 0.62), 0, 255);
                    rgba[o + 1] = (byte)Clamp((int)Math.Round(Math.Sqrt(v) * gF * 255.0 * 0.62), 0, 255);
                    rgba[o + 2] = (byte)Clamp((int)Math.Round(Math.Sqrt(v) * bF * 255.0 * 0.62), 0, 255);
                    rgba[o + 3] = 255;
                }
            });
            return rgba;
        }

        // -----------------------------------------------------------------------------
        //  Named features. Real IAU nomenclature (Bennu's features are named for birds
        //  and bird-like creatures from world mythology) plus the four OSIRIS-REx
        //  candidate sample sites.
        // -----------------------------------------------------------------------------
        public static Feature[] Features()
        {
            return new Feature[]
            {
                new Feature("Nightingale",  "Nightingale Crater",  56.0,   42.0, 11.0, 210, 200, 190),
                new Feature("Osprey",       "Osprey Crater",       11.0,   88.0, 10.0, 180, 172, 168),
                new Feature("Kingfisher",   "Kingfisher Crater",   11.0,   56.0,  8.0, 150, 146, 150),
                new Feature("Sandpiper",    "Sandpiper Crater",   -47.0,  322.0, 10.0, 120, 118, 126),
                new Feature("Benben",       "Benben Saxum",       -25.0,  212.0,  7.0, 235, 120,  60),
                new Feature("Roc",          "Roc Saxum",          -35.0,   30.0,  9.0, 220,  80,  70),
                new Feature("Tlanuwa",      "Tlanuwa Regio",      -20.0,  140.0, 22.0, 160,  95, 110),
                new Feature("Gargoyle",     "Gargoyle Saxum",     -33.0,   92.0,  6.0, 190, 140, 200),
            };
        }

        static byte[] BuildBiomeMap(Feature[] feats)
        {
            int w = Cfg.BiomeW, h = Cfg.BiomeH;
            byte[] rgba = new byte[w * h * 4];

            Color cRidge = Color.FromArgb(255, 90, 80, 75);
            Color cNorth = Color.FromArgb(255, 70, 72, 82);
            Color cSouth = Color.FromArgb(255, 82, 70, 66);
            Color cNPole = Color.FromArgb(255, 40, 44, 52);
            Color cSPole = Color.FromArgb(255, 52, 42, 40);

            for (int j = 0; j < h; j++)
            {
                double latDeg = ((j + 0.5) / h - 0.5) * 180.0;
                for (int i = 0; i < w; i++)
                {
                    double lonDeg = ((i + 0.5) / w - 0.5) * 360.0;

                    Color c;
                    double a = Math.Abs(latDeg);
                    if (a >= 70.0) c = latDeg > 0 ? cNPole : cSPole;
                    else if (a <= 12.0) c = cRidge;
                    else c = latDeg > 0 ? cNorth : cSouth;

                    // Named features overprint the zonal background.
                    for (int f = 0; f < feats.Length; f++)
                    {
                        if (AngDistDeg(latDeg, lonDeg, feats[f].Lat, feats[f].Lon) <= feats[f].AngRadiusDeg)
                        { c = feats[f].Col; break; }
                    }

                    int o = (j * w + i) * 4;
                    rgba[o + 0] = c.R; rgba[o + 1] = c.G; rgba[o + 2] = c.B; rgba[o + 3] = 255;
                }
            }
            return rgba;
        }

        static double AngDistDeg(double lat1, double lon1, double lat2, double lon2)
        {
            double d2r = Math.PI / 180.0;
            double a1 = lat1 * d2r, a2 = lat2 * d2r;
            double dl = (lon2 - lon1) * d2r;
            double v = Math.Sin(a1) * Math.Sin(a2) + Math.Cos(a1) * Math.Cos(a2) * Math.Cos(dl);
            return Math.Acos(Clamp(v, -1.0, 1.0)) / d2r;
        }

        // -----------------------------------------------------------------------------
        static void WriteDerived(string path, double datum, double deformity, Feature[] feats)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("// AUTO-GENERATED by Tools/Generate-BennuMaps.ps1 - do not hand-edit.");
            sb.AppendLine("// These are the numbers the shipped maps actually imply. Bennu.cfg must");
            sb.AppendLine("// agree with them or terrain and scaled space will not line up.");
            sb.AppendLine();
            sb.AppendLine(F("// scale factor vs. real Bennu     : {0:F2}x", Cfg.Scale));
            sb.AppendLine(F("// real mean radius                : {0:F1} m", Cfg.RealMean));
            sb.AppendLine(F("// Properties/radius   (datum)     : {0:F0}", datum));
            sb.AppendLine(F("// VertexHeightMap/deformity       : {0:F0}", deformity));
            sb.AppendLine(F("// surface radius range            : {0:F1} .. {1:F1} m", rMin, rMax));
            sb.AppendLine(F("// mean surface radius             : {0:F1} m", Cfg.RealMean * Cfg.Scale));
            sb.AppendLine(F("// Parallax minTerrainAltitude     : {0:F0}", 0.0));
            sb.AppendLine(F("// Parallax maxTerrainAltitude     : {0:F0}", deformity));
            sb.AppendLine();
            sb.AppendLine(F("// height map shipped at           : {0}x{1}", Cfg.HeightOutW, Cfg.HeightOutH));
            sb.AppendLine(F("// height quantisation step        : {0:F3} m  (deformity/255)", deformity / 255.0));
            sb.AppendLine(F("// height texel spacing            : {0:F2} m", 2.0 * Math.PI * Cfg.RealMean * Cfg.Scale / Cfg.HeightOutW));
            sb.AppendLine(F("// worst-case quantisation facet   : {0:F1} deg",
                Math.Atan((deformity / 255.0) / (2.0 * Math.PI * Cfg.RealMean * Cfg.Scale / Cfg.HeightOutW)) * 180.0 / Math.PI));
            sb.AppendLine();
            sb.AppendLine("// Biome colours (must match Properties/Biomes exactly):");
            foreach (Feature f in feats)
                sb.AppendLine(F("//   {0,-14} {1,3},{2,3},{3,3}", f.Name, f.Col.R, f.Col.G, f.Col.B));
            File.WriteAllText(path, sb.ToString());
        }

        static string F(string fmt, params object[] a)
        { return string.Format(CultureInfo.InvariantCulture, fmt, a); }

        // -----------------------------------------------------------------------------
        static void WritePngFlipped(string path, byte[] rgbaBottomUp, int w, int h)
        {
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                byte[] row = new byte[w * 4];
                for (int j = 0; j < h; j++)
                {
                    int src = (h - 1 - j) * w * 4;   // flip: PNG wants north first
                    for (int i = 0; i < w; i++)
                    {
                        // System.Drawing 32bppArgb is BGRA in memory.
                        row[i * 4 + 0] = rgbaBottomUp[src + i * 4 + 2];
                        row[i * 4 + 1] = rgbaBottomUp[src + i * 4 + 1];
                        row[i * 4 + 2] = rgbaBottomUp[src + i * 4 + 0];
                        row[i * 4 + 3] = rgbaBottomUp[src + i * 4 + 3];
                    }
                    System.Runtime.InteropServices.Marshal.Copy(
                        row, 0, IntPtr.Add(bd.Scan0, j * bd.Stride), row.Length);
                }
                bmp.UnlockBits(bd);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        internal static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        internal static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        static double SmoothStep(double e0, double e1, double x)
        {
            double t = Clamp((x - e0) / (e1 - e0), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }
        static void Log(string s) { Console.WriteLine("  " + s); }
    }
}

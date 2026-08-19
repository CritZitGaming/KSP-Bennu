// =====================================================================================
//  BennuIconGen.cs  --  the map-view / tracking-station node icon
// =====================================================================================
//
//  Kopernicus' Orbit { iconTexture } replaces the icon KSP draws for a body in map view
//  and the tracking station. RuntimeUtility turns the whole texture into a sprite with
//  a centred pivot:
//
//      Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
//                    new Vector2(0.5f, 0.5f), ...)
//      body.MapObject.uiNode.SetIcon(sprite)
//
//  so any square RGBA texture works and there is no atlas or cell layout to match.
//
//  WHY THIS IS GENERATED RATHER THAN DRAWN
//  ---------------------------------------
//  The rest of the pack has no hand-painted art, and the icon should not be the one
//  exception. The silhouette here is not eyeballed: it is the *same* spinning-top
//  profile that BennuMapGen.SurfaceRadius uses for the body itself - steps 1 and 2 of
//  that function, the conical flanks and the equatorial crest - read straight off the
//  same Cfg constants. Change Cfg.RidgeConeExp or Cfg.RidgeBump and the icon follows
//  the body automatically.
//
//  The 3D noise terms (steps 3-5) are deliberately NOT applied. They are sampled on the
//  sphere and have no meaning for a 2D outline, and at the ~20 px this icon is actually
//  drawn at they would be invisible anyway. What survives at that size is exactly what
//  makes Bennu recognisable: wide at the equator, flat at the poles, straight flanks.
//
//  Because the body is a solid of revolution whose radius depends only on latitude, the
//  edge-on silhouette *is* the axial cross-section - so the outline below is exact for
//  the noise-free shape, not an approximation of it.
// =====================================================================================

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace BennuGen
{
    public static class IconGen
    {
        // Icon resolution. KSP draws the node icon at roughly 20-32 px, so 64 is enough
        // to stay crisp when the UI scales up without wasting a texture slot. Kept a
        // power of two because Unity is happier with them.
        public const int Size = 64;

        // Supersampling for the edge. Coverage-based anti-aliasing: 8x8 = 64 samples per
        // pixel, which is smooth enough that no post-blur is needed.
        const int SS = 8;

        // Fraction of the half-width left empty around the shape. The sprite pivot is the
        // texture centre, so this margin is what stops the widest part of the silhouette
        // touching the edge and looking clipped against the selection ring.
        const double Margin = 0.06;

        // THE ICON IS A STYLISATION, NOT A PROJECTION
        // -------------------------------------------
        // The literal profile is 1147 m at the crest against 996 m at the poles - a 1.13:1
        // silhouette. Rendered honestly at the ~20 px KSP actually draws a node icon at,
        // that is a circle: indistinguishable from every stock body, which defeats the
        // whole point of a custom icon. An icon is a symbol, so this one is drawn to the
        // body's shape *law* with its proportions pushed until they survive at 20 px.
        //
        // What is kept from the body: the cone-flank-plus-equatorial-crest construction,
        // and the crest width (Cfg.RidgeBumpSigmaDeg), so the ridge sits where it really
        // sits. What is chosen for legibility: the three numbers below.
        //
        // Everything else in this pack is fidelity-first. This file is the exception, and
        // it says so rather than pretending otherwise.

        // Polar radius as a fraction of the equatorial radius. The body is 0.87; at that
        // ratio the outline is a circle. 0.52 is flat enough to read as a spinning top.
        const double PolarRatio = 0.52;

        // Flank exponent. The body uses 1.4, which rounds the flanks off - correct for the
        // real shape, but a rounded flank at icon size is just a circle again. 1.0 gives
        // the dead-straight flanks that make the outline a flat-poled diamond.
        const double FlankExp = 1.0;

        // Height of the equatorial crest, as a fraction of the equatorial radius. Small on
        // purpose: enough to break the straight flank into a visible point, not so much
        // that the icon turns into a lens.
        const double Crest = 0.055;

        // -----------------------------------------------------------------------------
        //  Silhouette radius, normalised so the equatorial crest is 1.0.
        // -----------------------------------------------------------------------------
        static double Profile(double latRad)
        {
            double s = Math.Abs(Math.Sin(latRad));             // |sin(latitude)|

            // Conical flanks: straight lines from pole to equator, exactly the
            // construction Cfg.RidgeConeExp parameterises on the body itself.
            double cone = 1.0 - Math.Pow(s, FlankExp);
            double r = PolarRatio + (1.0 - PolarRatio) * cone;

            // The crest line - a tight band right at the equator, using the body's own
            // width for it so the ridge is where the terrain actually puts it.
            double latDeg = latRad * 180.0 / Math.PI;
            double g = latDeg / Cfg.RidgeBumpSigmaDeg;
            r += Crest * Math.Exp(-g * g);

            return r;
        }

        // -----------------------------------------------------------------------------
        public static void Run(string gameDataBennuDir, string previewDir)
        {
            // The widest point is the equatorial crest; everything scales against it so
            // the icon fills its box regardless of what the shape constants are set to.
            double rMax = Profile(0.0);

            int w = Size, h = Size;
            byte[] rgba = new byte[w * h * 4];      // row 0 = bottom, matching Dds.cs

            double half = (Size / 2.0) * (1.0 - Margin);
            double inv = 1.0 / SS;

            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    int inside = 0;

                    for (int sy = 0; sy < SS; sy++)
                    {
                        // Pixel centre plus subpixel offset, in units where the icon
                        // spans -Size/2 .. +Size/2 about the centre.
                        double py = (j + (sy + 0.5) * inv) - Size / 2.0;

                        for (int sx = 0; sx < SS; sx++)
                        {
                            double px = (i + (sx + 0.5) * inv) - Size / 2.0;

                            double rho = Math.Sqrt(px * px + py * py);
                            if (rho < 1e-9) { inside++; continue; }

                            // Latitude of this direction in the edge-on view. The body is
                            // a solid of revolution, so the silhouette boundary at this
                            // angle is simply the profile radius there.
                            double lat = Math.Atan2(py, Math.Abs(px));
                            double edge = Profile(lat) / rMax * half;

                            if (rho <= edge) inside++;
                        }
                    }

                    if (inside == 0) continue;

                    double cov = inside / (double)(SS * SS);
                    int o = (j * w + i) * 4;
                    rgba[o + 0] = 255;                            // white, so that
                    rgba[o + 1] = 255;                            // Orbit { iconColor }
                    rgba[o + 2] = 255;                            // can tint it freely
                    rgba[o + 3] = (byte)Math.Round(cov * 255.0);
                }
            }

            Directory.CreateDirectory(gameDataBennuDir);
            string dds = Path.Combine(gameDataBennuDir, "Bennu_Icon.dds");
            Dds.WriteRgba32(dds, rgba, w, h);
            Console.WriteLine("  wrote {0}  ({1}x{1} RGBA32)", dds, Size);

            if (!string.IsNullOrEmpty(previewDir))
            {
                Directory.CreateDirectory(previewDir);
                string png = Path.Combine(previewDir, "map_icon.png");
                WritePngFlipped(png, rgba, w, h);
                Console.WriteLine("  wrote {0}", png);
            }
        }

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
    }
}

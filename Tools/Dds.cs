// =====================================================================================
//  Minimal DDS writer targeting exactly the formats KSP/Kopernicus/Parallax already use.
//
//    WriteL8      - 8-bit luminance, uncompressed, no mips.  Height maps (MapSO).
//                   Matches Parallax_StockPlanetTextures/Gilly/PluginData/Gilly_Height.dds
//    WriteRgba32  - 32-bit uncompressed, no mips.            Biome maps (exact colours).
//    WriteDxt1    - BC1 + mipmaps.                           Scaled-space albedo.
//                   Matches .../Gilly_Color.dds
//    WriteDxt5    - BC3 + mipmaps, optional normal-map hint. Scaled-space normals.
//                   Matches .../Gilly_Normal.dds (pixel-format flags 0x80000004)
//
//  Input rasters are RGBA bytes, row 0 = south pole, written in that order. See the
//  orientation note at the top of BennuMapGen.cs.
// =====================================================================================

using System;
using System.IO;
using System.Threading.Tasks;

namespace BennuGen
{
    public static class Dds
    {
        const uint MAGIC = 0x20534444; // "DDS "

        const uint DDSD_CAPS = 0x1, DDSD_HEIGHT = 0x2, DDSD_WIDTH = 0x4, DDSD_PITCH = 0x8;
        const uint DDSD_PIXELFORMAT = 0x1000, DDSD_MIPMAPCOUNT = 0x20000, DDSD_LINEARSIZE = 0x80000;

        const uint DDPF_ALPHAPIXELS = 0x1, DDPF_FOURCC = 0x4, DDPF_RGB = 0x40, DDPF_LUMINANCE = 0x20000;
        const uint DDPF_NORMAL = 0x80000000;

        const uint DDSCAPS_COMPLEX = 0x8, DDSCAPS_TEXTURE = 0x1000, DDSCAPS_MIPMAP = 0x400000;

        // -----------------------------------------------------------------------------
        static void WriteHeader(BinaryWriter w, int width, int height, int mips,
                                uint pitchOrLinear, bool linearSize,
                                uint pfFlags, string fourCC, uint bitCount,
                                uint rM, uint gM, uint bM, uint aM)
        {
            uint flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT
                       | (linearSize ? DDSD_LINEARSIZE : DDSD_PITCH);
            if (mips > 1) flags |= DDSD_MIPMAPCOUNT;

            w.Write(MAGIC);
            w.Write((uint)124);
            w.Write(flags);
            w.Write((uint)height);
            w.Write((uint)width);
            w.Write(pitchOrLinear);
            w.Write((uint)0);              // depth
            w.Write((uint)(mips > 1 ? mips : 0));
            for (int i = 0; i < 11; i++) w.Write((uint)0);   // reserved

            // DDS_PIXELFORMAT
            w.Write((uint)32);
            w.Write(pfFlags);
            if (fourCC != null && fourCC.Length == 4)
            {
                w.Write((byte)fourCC[0]); w.Write((byte)fourCC[1]);
                w.Write((byte)fourCC[2]); w.Write((byte)fourCC[3]);
            }
            else w.Write((uint)0);
            w.Write(bitCount);
            w.Write(rM); w.Write(gM); w.Write(bM); w.Write(aM);

            uint caps = DDSCAPS_TEXTURE;
            if (mips > 1) caps |= DDSCAPS_COMPLEX | DDSCAPS_MIPMAP;
            w.Write(caps);
            w.Write((uint)0); w.Write((uint)0); w.Write((uint)0); w.Write((uint)0);
        }

        // -----------------------------------------------------------------------------
        public static void WriteL8(string path, byte[] lum, int w, int h)
        {
            using (BinaryWriter bw = new BinaryWriter(File.Create(path)))
            {
                WriteHeader(bw, w, h, 1, (uint)w, false,
                            DDPF_LUMINANCE, null, 8, 0x000000FF, 0, 0, 0);
                bw.Write(lum, 0, w * h);
            }
        }

        public static void WriteRgba32(string path, byte[] rgba, int w, int h)
        {
            using (BinaryWriter bw = new BinaryWriter(File.Create(path)))
            {
                WriteHeader(bw, w, h, 1, (uint)(w * 4), false,
                            DDPF_RGB | DDPF_ALPHAPIXELS, null, 32,
                            0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000);
                bw.Write(rgba, 0, w * h * 4);
            }
        }

        // -----------------------------------------------------------------------------
        public static void WriteDxt1(string path, byte[] rgba, int w, int h)
        { WriteDxt(path, rgba, w, h, false, false); }

        public static void WriteDxt5(string path, byte[] rgba, int w, int h, bool normalMap)
        { WriteDxt(path, rgba, w, h, true, normalMap); }

        static void WriteDxt(string path, byte[] rgba, int w, int h, bool dxt5, bool normalMap)
        {
            int mips = MipCount(w, h);
            int blockBytes = dxt5 ? 16 : 8;
            uint linear = (uint)(Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes);

            uint pf = DDPF_FOURCC | (normalMap ? DDPF_NORMAL : 0u);

            using (BinaryWriter bw = new BinaryWriter(File.Create(path)))
            {
                WriteHeader(bw, w, h, mips, linear, true, pf, dxt5 ? "DXT5" : "DXT1", 0, 0, 0, 0, 0);

                byte[] cur = rgba; int cw = w, ch = h;
                for (int m = 0; m < mips; m++)
                {
                    bw.Write(CompressLevel(cur, cw, ch, dxt5));
                    if (m == mips - 1) break;
                    cur = Downsample(cur, cw, ch, out cw, out ch);
                }
            }
        }

        static int MipCount(int w, int h)
        {
            int n = 1;
            while (w > 1 || h > 1) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); n++; }
            return n;
        }

        /// <summary>Box-filter halving. Mips are what stop scaled space shimmering.</summary>
        static byte[] Downsample(byte[] src, int w, int h, out int nw, out int nh)
        {
            nw = Math.Max(1, w / 2); nh = Math.Max(1, h / 2);
            byte[] dst = new byte[nw * nh * 4];
            int fnw = nw, fnh = nh;
            Parallel.For(0, fnh, j =>
            {
                for (int i = 0; i < fnw; i++)
                {
                    int x0 = Math.Min(w - 1, i * 2), x1 = Math.Min(w - 1, i * 2 + 1);
                    int y0 = Math.Min(h - 1, j * 2), y1 = Math.Min(h - 1, j * 2 + 1);
                    for (int c = 0; c < 4; c++)
                    {
                        int s = src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c]
                              + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c];
                        dst[(j * fnw + i) * 4 + c] = (byte)((s + 2) >> 2);
                    }
                }
            });
            return dst;
        }

        // -----------------------------------------------------------------------------
        static byte[] CompressLevel(byte[] rgba, int w, int h, bool dxt5)
        {
            int bx = Math.Max(1, (w + 3) / 4), by = Math.Max(1, (h + 3) / 4);
            int blockBytes = dxt5 ? 16 : 8;
            byte[] outBuf = new byte[bx * by * blockBytes];

            Parallel.For(0, by, byi =>
            {
                byte[] blk = new byte[16 * 4];
                for (int bxi = 0; bxi < bx; bxi++)
                {
                    // Gather the 4x4 texels, clamping at the edges of non-multiple-of-4 sizes.
                    for (int j = 0; j < 4; j++)
                    {
                        int sy = Math.Min(h - 1, byi * 4 + j);
                        for (int i = 0; i < 4; i++)
                        {
                            int sx = Math.Min(w - 1, bxi * 4 + i);
                            int s = (sy * w + sx) * 4, d = (j * 4 + i) * 4;
                            blk[d] = rgba[s]; blk[d + 1] = rgba[s + 1];
                            blk[d + 2] = rgba[s + 2]; blk[d + 3] = rgba[s + 3];
                        }
                    }

                    int o = (byi * bx + bxi) * blockBytes;
                    if (dxt5) { EncodeAlphaBlock(blk, outBuf, o); EncodeColorBlock(blk, outBuf, o + 8); }
                    else EncodeColorBlock(blk, outBuf, o);
                }
            });
            return outBuf;
        }

        // ---- BC1 colour block --------------------------------------------------------
        static void EncodeColorBlock(byte[] blk, byte[] dst, int o)
        {
            // Bounding box in RGB, then inset slightly: a cheap approximation of the
            // principal axis that avoids washing out block contrast.
            int rMin = 255, gMin = 255, bMin = 255, rMax = 0, gMax = 0, bMax = 0;
            for (int i = 0; i < 16; i++)
            {
                int r = blk[i * 4], g = blk[i * 4 + 1], b = blk[i * 4 + 2];
                if (r < rMin) rMin = r; if (r > rMax) rMax = r;
                if (g < gMin) gMin = g; if (g > gMax) gMax = g;
                if (b < bMin) bMin = b; if (b > bMax) bMax = b;
            }
            int ir = (rMax - rMin) >> 4, ig = (gMax - gMin) >> 4, ib = (bMax - bMin) >> 4;
            rMin = Math.Min(255, rMin + ir); rMax = Math.Max(0, rMax - ir);
            gMin = Math.Min(255, gMin + ig); gMax = Math.Max(0, gMax - ig);
            bMin = Math.Min(255, bMin + ib); bMax = Math.Max(0, bMax - ib);

            ushort c0 = To565(rMax, gMax, bMax);
            ushort c1 = To565(rMin, gMin, bMin);

            // c0 > c1 selects the opaque 4-colour decode mode; the palette below is
            // built from whichever ordering we end up with, so indices stay correct.
            if (c0 < c1) { ushort t = c0; c0 = c1; c1 = t; }

            // Build the 4-colour palette exactly as the hardware decoder will.
            int[] pr = new int[4], pg = new int[4], pb = new int[4];
            From565(c0, out pr[0], out pg[0], out pb[0]);
            From565(c1, out pr[1], out pg[1], out pb[1]);
            if (c0 > c1)
            {
                pr[2] = (2 * pr[0] + pr[1]) / 3; pg[2] = (2 * pg[0] + pg[1]) / 3; pb[2] = (2 * pb[0] + pb[1]) / 3;
                pr[3] = (pr[0] + 2 * pr[1]) / 3; pg[3] = (pg[0] + 2 * pg[1]) / 3; pb[3] = (pb[0] + 2 * pb[1]) / 3;
            }
            else
            {
                pr[2] = (pr[0] + pr[1]) / 2; pg[2] = (pg[0] + pg[1]) / 2; pb[2] = (pb[0] + pb[1]) / 2;
                pr[3] = 0; pg[3] = 0; pb[3] = 0;
            }

            uint bits = 0;
            for (int i = 0; i < 16; i++)
            {
                int r = blk[i * 4], g = blk[i * 4 + 1], b = blk[i * 4 + 2];
                int best = 0, bestD = int.MaxValue;
                for (int k = 0; k < 4; k++)
                {
                    int dr = r - pr[k], dg = g - pg[k], db = b - pb[k];
                    // Luma-ish weighting matches perceived error better than a flat sum.
                    int d = 3 * dr * dr + 6 * dg * dg + db * db;
                    if (d < bestD) { bestD = d; best = k; }
                }
                bits |= (uint)best << (i * 2);
            }

            dst[o] = (byte)(c0 & 0xFF); dst[o + 1] = (byte)(c0 >> 8);
            dst[o + 2] = (byte)(c1 & 0xFF); dst[o + 3] = (byte)(c1 >> 8);
            dst[o + 4] = (byte)(bits & 0xFF);
            dst[o + 5] = (byte)((bits >> 8) & 0xFF);
            dst[o + 6] = (byte)((bits >> 16) & 0xFF);
            dst[o + 7] = (byte)((bits >> 24) & 0xFF);
        }

        // ---- BC4 alpha block (the A half of BC3) -------------------------------------
        static void EncodeAlphaBlock(byte[] blk, byte[] dst, int o)
        {
            int aMin = 255, aMax = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = blk[i * 4 + 3];
                if (a < aMin) aMin = a;
                if (a > aMax) aMax = a;
            }
            byte a0 = (byte)aMax, a1 = (byte)aMin;
            dst[o] = a0; dst[o + 1] = a1;

            int[] pal = new int[8];
            pal[0] = a0; pal[1] = a1;
            if (a0 > a1) for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            else
            {
                for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                pal[6] = 0; pal[7] = 255;
            }

            ulong bits = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = blk[i * 4 + 3];
                int best = 0, bestD = int.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    int d = a - pal[k]; d = d < 0 ? -d : d;
                    if (d < bestD) { bestD = d; best = k; }
                }
                bits |= (ulong)(uint)best << (i * 3);
            }
            for (int i = 0; i < 6; i++) dst[o + 2 + i] = (byte)((bits >> (i * 8)) & 0xFF);
        }

        static ushort To565(int r, int g, int b)
        {
            r = Clamp(r, 0, 255); g = Clamp(g, 0, 255); b = Clamp(b, 0, 255);
            return (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
        }

        static void From565(ushort c, out int r, out int g, out int b)
        {
            int r5 = (c >> 11) & 0x1F, g6 = (c >> 5) & 0x3F, b5 = c & 0x1F;
            r = (r5 << 3) | (r5 >> 2);
            g = (g6 << 2) | (g6 >> 4);
            b = (b5 << 3) | (b5 >> 2);
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}

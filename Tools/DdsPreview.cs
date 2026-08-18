// =====================================================================================
//  DdsPreview - reads the DDS files back, decodes them, and writes PNG previews.
//
//  This exists to validate the encoder in Dds.cs without launching KSP: if these
//  previews decode and look right, the block layout, endpoint ordering and header
//  flags are correct. It also renders a shaded 3D view of the shape model so the
//  silhouette can be checked against real Bennu imagery.
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace BennuGen
{
    public static class Preview
    {
        public class Img
        {
            public int W, H;
            public byte[] Rgba;   // row 0 = south pole (same convention as the DDS)
        }

        // -----------------------------------------------------------------------------
        public static string Describe(string path)
        {
            using (BinaryReader br = new BinaryReader(File.OpenRead(path)))
            {
                uint magic = br.ReadUInt32();
                if (magic != 0x20534444) return path + ": NOT A DDS";
                br.ReadUInt32();
                uint flags = br.ReadUInt32();
                int h = (int)br.ReadUInt32(), w = (int)br.ReadUInt32();
                br.ReadUInt32(); br.ReadUInt32();
                uint mips = br.ReadUInt32();
                br.ReadBytes(44);
                br.ReadUInt32();
                uint pfFlags = br.ReadUInt32();
                string fourcc = new string(new char[] {
                    (char)br.ReadByte(), (char)br.ReadByte(), (char)br.ReadByte(), (char)br.ReadByte() });
                uint bits = br.ReadUInt32();
                return string.Format("{0,-20} {1,5}x{2,-5} mips={3,-3} pfFlags=0x{4:X8} fourcc='{5}' bits={6}",
                    Path.GetFileName(path), w, h, mips, pfFlags, fourcc.Trim('\0'), bits);
            }
        }

        // -----------------------------------------------------------------------------
        public static Img Load(string path)
        {
            using (BinaryReader br = new BinaryReader(File.OpenRead(path)))
            {
                if (br.ReadUInt32() != 0x20534444) throw new Exception("not dds: " + path);
                br.ReadUInt32(); br.ReadUInt32();
                int h = (int)br.ReadUInt32(), w = (int)br.ReadUInt32();
                br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
                br.ReadBytes(44);
                br.ReadUInt32();
                uint pfFlags = br.ReadUInt32();
                byte[] fc = br.ReadBytes(4);
                string fourcc = System.Text.Encoding.ASCII.GetString(fc);
                uint bits = br.ReadUInt32();
                br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
                br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();

                Img img = new Img { W = w, H = h, Rgba = new byte[w * h * 4] };

                if ((pfFlags & 0x4) != 0 && fourcc.StartsWith("DXT"))
                {
                    bool dxt5 = fourcc == "DXT5";
                    int bx = (w + 3) / 4, by = (h + 3) / 4;
                    byte[] data = br.ReadBytes(bx * by * (dxt5 ? 16 : 8));
                    DecodeDxt(data, img, dxt5);
                }
                else if ((pfFlags & 0x20000) != 0 && bits == 8)
                {
                    byte[] data = br.ReadBytes(w * h);
                    for (int i = 0; i < w * h; i++)
                    {
                        img.Rgba[i * 4] = img.Rgba[i * 4 + 1] = img.Rgba[i * 4 + 2] = data[i];
                        img.Rgba[i * 4 + 3] = 255;
                    }
                }
                else if (bits == 32)
                {
                    img.Rgba = br.ReadBytes(w * h * 4);
                }
                else throw new Exception("unsupported dds format in " + path);

                return img;
            }
        }

        static void DecodeDxt(byte[] data, Img img, bool dxt5)
        {
            int bx = (img.W + 3) / 4, by = (img.H + 3) / 4;
            int stride = dxt5 ? 16 : 8;
            for (int byi = 0; byi < by; byi++)
                for (int bxi = 0; bxi < bx; bxi++)
                {
                    int o = (byi * bx + bxi) * stride;
                    byte[] alpha = new byte[16];
                    for (int i = 0; i < 16; i++) alpha[i] = 255;

                    if (dxt5)
                    {
                        int a0 = data[o], a1 = data[o + 1];
                        int[] pal = new int[8];
                        pal[0] = a0; pal[1] = a1;
                        if (a0 > a1) for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                        else
                        {
                            for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                            pal[6] = 0; pal[7] = 255;
                        }
                        ulong bits = 0;
                        for (int i = 0; i < 6; i++) bits |= (ulong)data[o + 2 + i] << (i * 8);
                        for (int i = 0; i < 16; i++) alpha[i] = (byte)pal[(int)((bits >> (i * 3)) & 7)];
                        o += 8;
                    }

                    ushort c0 = (ushort)(data[o] | (data[o + 1] << 8));
                    ushort c1 = (ushort)(data[o + 2] | (data[o + 3] << 8));
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
                        pr[3] = pg[3] = pb[3] = 0;
                    }
                    uint idx = (uint)(data[o + 4] | (data[o + 5] << 8) | (data[o + 6] << 16) | (data[o + 7] << 24));

                    for (int j = 0; j < 4; j++)
                        for (int i = 0; i < 4; i++)
                        {
                            int px = bxi * 4 + i, py = byi * 4 + j;
                            if (px >= img.W || py >= img.H) continue;
                            int k = j * 4 + i;
                            int s = (int)((idx >> (k * 2)) & 3);
                            int d = (py * img.W + px) * 4;
                            img.Rgba[d] = (byte)pr[s];
                            img.Rgba[d + 1] = (byte)pg[s];
                            img.Rgba[d + 2] = (byte)pb[s];
                            img.Rgba[d + 3] = alpha[k];
                        }
                }
        }

        static void From565(ushort c, out int r, out int g, out int b)
        {
            int r5 = (c >> 11) & 0x1F, g6 = (c >> 5) & 0x3F, b5 = c & 0x1F;
            r = (r5 << 3) | (r5 >> 2); g = (g6 << 2) | (g6 >> 4); b = (b5 << 3) | (b5 >> 2);
        }

        /// <summary>Bilinear sample of the red channel, 0..1. Wraps in u, clamps in v -
        /// the same filtering KSP's MapSO applies to the height map in game.</summary>
        static double SampleBilinear(Img img, double u, double v)
        {
            double x = u * img.W - 0.5, y = v * img.H - 0.5;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            int x1 = ((x0 + 1) % img.W + img.W) % img.W;
            x0 = (x0 % img.W + img.W) % img.W;
            int y1 = Math.Max(0, Math.Min(img.H - 1, y0 + 1));
            y0 = Math.Max(0, Math.Min(img.H - 1, y0));

            double h00 = img.Rgba[(y0 * img.W + x0) * 4] / 255.0;
            double h10 = img.Rgba[(y0 * img.W + x1) * 4] / 255.0;
            double h01 = img.Rgba[(y1 * img.W + x0) * 4] / 255.0;
            double h11 = img.Rgba[(y1 * img.W + x1) * 4] / 255.0;
            return (h00 * (1 - fx) + h10 * fx) * (1 - fy) + (h01 * (1 - fx) + h11 * fx) * fy;
        }

        // -----------------------------------------------------------------------------
        //  Slope statistics, in degrees, measured off the shipped height map.
        //
        //  This is the number that decides whether the body is landable - not relief.
        //  Landing legs cope with roughly 15 degrees before a craft starts sliding or
        //  tipping, and low gravity makes that worse rather than better, so the median
        //  wants to sit well under that with the 95th percentile still sane.
        //
        //  Note this measures the height map only. The PQS noise mods in Bennu.cfg and
        //  Parallax's tessellation displacement add more on top at finer scales.
        // -----------------------------------------------------------------------------
        public static string SlopeStats(Img height, double datum, double deformity)
        {
            int W = height.W, H = height.H;
            double meanR = datum + deformity * 0.3;
            double dLat = Math.PI / H, dLon = 2.0 * Math.PI / W;
            double dyPhys = meanR * dLat;

            List<double> slopes = new List<double>();
            // Every 4th texel is plenty for a distribution and keeps this quick.
            for (int j = 2; j < H - 2; j += 4)
            {
                double lat = ((j + 0.5) / H - 0.5) * Math.PI;
                double dxPhys = Math.Max(meanR * Math.Cos(lat) * dLon, 0.35 * dyPhys);
                for (int i = 0; i < W; i += 4)
                {
                    int ip = (i + 1) % W, im = (i - 1 + W) % W;
                    double gx = (height.Rgba[(j * W + ip) * 4] - height.Rgba[(j * W + im) * 4])
                                / 255.0 * deformity / (2.0 * dxPhys);
                    double gy = (height.Rgba[((j + 1) * W + i) * 4] - height.Rgba[((j - 1) * W + i) * 4])
                                / 255.0 * deformity / (2.0 * dyPhys);
                    slopes.Add(Math.Atan(Math.Sqrt(gx * gx + gy * gy)) * 180.0 / Math.PI);
                }
            }
            slopes.Sort();
            Func<double, double> pct = p => slopes[(int)Math.Min(slopes.Count - 1, p * slopes.Count)];

            int over15 = 0, over30 = 0;
            foreach (double s in slopes) { if (s > 15) over15++; if (s > 30) over30++; }

            return string.Format(
                "  median {0,5:F1} deg | 90th {1,5:F1} | 99th {2,5:F1} | max {3,5:F1}\n" +
                "  {4,5:F1}% of surface steeper than 15 deg, {5,4:F1}% steeper than 30 deg",
                pct(0.5), pct(0.9), pct(0.99), slopes[slopes.Count - 1],
                100.0 * over15 / slopes.Count, 100.0 * over30 / slopes.Count);
        }

        // -----------------------------------------------------------------------------
        public static void SavePng(Img img, string path, int maxW)
        {
            int scale = Math.Max(1, img.W / maxW);
            int w = img.W / scale, h = img.H / scale;
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                byte[] row = new byte[w * 4];
                for (int j = 0; j < h; j++)
                {
                    int sy = (h - 1 - j) * scale;   // flip so north ends up at the top
                    for (int i = 0; i < w; i++)
                    {
                        int s = (sy * img.W + i * scale) * 4;
                        row[i * 4 + 0] = img.Rgba[s + 2];
                        row[i * 4 + 1] = img.Rgba[s + 1];
                        row[i * 4 + 2] = img.Rgba[s + 0];
                        row[i * 4 + 3] = 255;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(bd.Scan0, j * bd.Stride), row.Length);
                }
                bmp.UnlockBits(bd);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        // -----------------------------------------------------------------------------
        //  Shaded orthographic render of the ACTUAL displaced geometry, so the true
        //  silhouette - the equatorial ridge in particular - can be eyeballed against
        //  real Bennu photographs. Every height-map texel becomes a point, projected
        //  with a z-buffer; at 4096x2048 that is 8.4M points into a ~700px frame, which
        //  is dense enough to leave no holes.
        // -----------------------------------------------------------------------------
        public static void RenderGlobe(Img height, Img color, string path, int size,
                                       double datum, double deformity, double lonOffsetDeg, double viewLatDeg)
        {
            double d2r = Math.PI / 180.0;
            double vlat = viewLatDeg * d2r;

            // Camera basis. fwd points from the body toward the viewer.
            double[] up = { 0, -Math.Sin(vlat), Math.Cos(vlat) };
            double[] fwd = { 0, Math.Cos(vlat), Math.Sin(vlat) };
            double[] rgt = { 1, 0, 0 };

            double maxR = datum + deformity;
            double[] zbuf = new double[size * size];
            byte[] frame = new byte[size * size * 4];
            for (int i = 0; i < zbuf.Length; i++) zbuf[i] = double.NegativeInfinity;
            for (int i = 0; i < size * size; i++)
            {
                frame[i * 4 + 0] = 10; frame[i * 4 + 1] = 9; frame[i * 4 + 2] = 8; frame[i * 4 + 3] = 255;
            }

            // Sun: high and to the left, roughly matching OSIRIS-REx approach imagery.
            double lx = -0.60, ly = 0.30, lz = 0.74;
            double ll = Math.Sqrt(lx * lx + ly * ly + lz * lz); lx /= ll; ly /= ll; lz /= ll;

            int W = height.W, H = height.H;
            double mean = datum + deformity * 0.3;

            // Splat a grid dense enough to leave no holes in the frame, sampling the
            // height map bilinearly. The height map is deliberately low-resolution (see
            // Cfg.HeightOutW), so iterating its texels directly would splat far fewer
            // points than the frame has pixels and produce a dotted image.
            int GW = Math.Max(W, size * 5), GH = GW / 2;

            for (int j = 0; j < GH; j++)
            {
                double lat = ((j + 0.5) / GH - 0.5) * Math.PI;
                double cosLat = Math.Cos(lat), sinLat = Math.Sin(lat);

                for (int i = 0; i < GW; i++)
                {
                    double lon = ((i + 0.5) / GW - 0.5) * 2.0 * Math.PI + lonOffsetDeg * d2r;
                    double r = datum + deformity * SampleBilinear(height, (i + 0.5) / GW, (j + 0.5) / GH);

                    double ux = cosLat * Math.Cos(lon), uy = cosLat * Math.Sin(lon), uz = sinLat;
                    double wx = ux * r, wy = uy * r, wz = uz * r;

                    double sx = (wx * rgt[0] + wy * rgt[1] + wz * rgt[2]) / maxR;
                    double sy = (wx * up[0] + wy * up[1] + wz * up[2]) / maxR;
                    double sz = wx * fwd[0] + wy * fwd[1] + wz * fwd[2];

                    int px = (int)((sx * 0.5 + 0.5) * size);
                    int py = (int)((0.5 - sy * 0.5) * size);
                    if (px < 0 || py < 0 || px >= size || py >= size) continue;

                    int zi = py * size + px;
                    if (sz <= zbuf[zi]) continue;
                    zbuf[zi] = sz;

                    // Surface normal from the height gradient. Taken at the height map's
                    // own resolution - that is the real geometry; the grid above only
                    // controls splat density.
                    int hi = (int)((i + 0.5) / GW * W); if (hi >= W) hi = W - 1;
                    int hj = (int)((j + 0.5) / GH * H); if (hj >= H) hj = H - 1;
                    int ip = (hi + 1) % W, im = (hi - 1 + W) % W;
                    int jp = Math.Min(H - 1, hj + 1), jm = Math.Max(0, hj - 1);
                    double dRdLon = (height.Rgba[(hj * W + ip) * 4] - height.Rgba[(hj * W + im) * 4])
                                    / 255.0 * deformity / (2.0 * (2 * Math.PI / W));
                    double dRdLat = (height.Rgba[(jp * W + hi) * 4] - height.Rgba[(jm * W + hi) * 4])
                                    / 255.0 * deformity / ((jp - jm) * (Math.PI / H));

                    // Clamp the east-west spacing near the poles for the same reason
                    // the generator does - see BuildNormalMap in BennuMapGen.cs.
                    double dyP = mean * (Math.PI / H);
                    double dxP = Math.Max(mean * cosLat * (2 * Math.PI / W), 0.35 * dyP);
                    double tx = -dRdLon * (2 * Math.PI / W) / dxP;
                    double ty = -dRdLat * (Math.PI / H) / dyP;

                    double ex0 = -Math.Sin(lon), ex1 = Math.Cos(lon), ex2 = 0;
                    double ey0 = -sinLat * Math.Cos(lon), ey1 = -sinLat * Math.Sin(lon), ey2 = cosLat;

                    double nx = ux + ex0 * tx + ey0 * ty;
                    double ny = uy + ex1 * tx + ey1 * ty;
                    double nz = uz + ex2 * tx + ey2 * ty;
                    double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    nx /= nl; ny /= nl; nz /= nl;

                    double ndl = Math.Max(0.0, nx * lx + ny * ly + nz * lz);

                    int ci = (int)((i / (double)GW) * color.W); if (ci >= color.W) ci = color.W - 1;
                    int cj = (int)((j / (double)GH) * color.H); if (cj >= color.H) cj = color.H - 1;
                    int cO = (cj * color.W + ci) * 4;

                    // Published Bennu images are stretched well past true reflectance;
                    // lift a little here too or a 0.044-albedo body is just a black
                    // disc, but keep it modest so the preview stays representative.
                    double gain = 2.3;
                    double amb = 0.04;
                    double shade = ndl * gain + amb;

                    int rr = (int)Math.Min(255.0, color.Rgba[cO + 0] * shade);
                    int gg = (int)Math.Min(255.0, color.Rgba[cO + 1] * shade);
                    int bb = (int)Math.Min(255.0, color.Rgba[cO + 2] * shade);

                    frame[zi * 4 + 0] = (byte)bb;
                    frame[zi * 4 + 1] = (byte)gg;
                    frame[zi * 4 + 2] = (byte)rr;
                    frame[zi * 4 + 3] = 255;
                }
            }

            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int j = 0; j < size; j++)
                    Marshal.Copy(frame, j * size * 4, IntPtr.Add(bd.Scan0, j * bd.Stride), size * 4);
                bmp.UnlockBits(bd);
                bmp.Save(path, ImageFormat.Png);
            }
        }

    }
}

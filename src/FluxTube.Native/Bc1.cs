namespace FluxTube;

/// <summary>
/// BC1 (DXT1) block compression: 4 bits per pixel, decoded by the GPU itself.
/// A thumbnail takes an eighth of the video memory and disk space of raw pixels and is uploaded without any decoding.
/// Encoder: principal axis of each 4x4 block, endpoints at the extremes, one least-squares refinement (the stb_dxt recipe).
/// </summary>
public static class Bc1
{
    public static int Size(int w, int h) => (w / 4) * (h / 4) * 8;

    /// <param name="bgra">w*h*4 bytes, opaque; w and h multiples of 4.</param>
    public static void Encode(ReadOnlySpan<byte> bgra, int w, int h, Span<byte> dst)
    {
        Span<int> px = stackalloc int[48]; // 16 pixels, r g b
        Span<int> pal = stackalloc int[12];
        int o = 0;
        for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4)
            {
                int sr = 0, sg = 0, sb = 0;
                int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
                for (int y = 0; y < 4; y++)
                {
                    int row = ((by + y) * w + bx) * 4;
                    for (int x = 0; x < 4; x++)
                    {
                        int b = bgra[row + x * 4], g = bgra[row + x * 4 + 1], r = bgra[row + x * 4 + 2];
                        int i = (y * 4 + x) * 3;
                        px[i] = r; px[i + 1] = g; px[i + 2] = b;
                        sr += r; sg += g; sb += b;
                        if (r < minR) minR = r; if (r > maxR) maxR = r;
                        if (g < minG) minG = g; if (g > maxG) maxG = g;
                        if (b < minB) minB = b; if (b > maxB) maxB = b;
                    }
                }
                ushort c0, c1;
                uint mask = 0;
                if (minR == maxR && minG == maxG && minB == maxB)
                {
                    c0 = c1 = To565(minR, minG, minB);
                }
                else
                {
                    // principal axis by power iteration on the covariance matrix, seeded with the bounding-box diagonal
                    float mr = sr / 16f, mg = sg / 16f, mb = sb / 16f;
                    float crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
                    for (int i = 0; i < 48; i += 3)
                    {
                        float r = px[i] - mr, g = px[i + 1] - mg, b = px[i + 2] - mb;
                        crr += r * r; crg += r * g; crb += r * b; cgg += g * g; cgb += g * b; cbb += b * b;
                    }
                    float ar = maxR - minR, ag = maxG - minG, ab = maxB - minB;
                    for (int it = 0; it < 4; it++)
                    {
                        float r = ar * crr + ag * crg + ab * crb, g = ar * crg + ag * cgg + ab * cgb, b = ar * crb + ag * cgb + ab * cbb;
                        // rescaled every step: the vector grows by ~1e6 per iteration and its square would overflow a float
                        float big = MathF.Max(MathF.Abs(r), MathF.Max(MathF.Abs(g), MathF.Abs(b)));
                        if (big < 1e-6f) break;
                        ar = r / big; ag = g / big; ab = b / big;
                    }
                    float len = MathF.Sqrt(ar * ar + ag * ag + ab * ab);
                    if (len < 1e-3f) { ar = 0.299f; ag = 0.587f; ab = 0.114f; } else { ar /= len; ag /= len; ab /= len; }
                    float tmin = float.MaxValue, tmax = float.MinValue;
                    int imin = 0, imax = 0;
                    for (int i = 0; i < 48; i += 3)
                    {
                        float t = px[i] * ar + px[i + 1] * ag + px[i + 2] * ab;
                        if (t < tmin) { tmin = t; imin = i; }
                        if (t > tmax) { tmax = t; imax = i; }
                    }
                    c0 = To565(px[imax], px[imax + 1], px[imax + 2]);
                    c1 = To565(px[imin], px[imin + 1], px[imin + 2]);
                    mask = Match(px, pal, c0, c1);
                    if (Refine(px, mask, ref c0, ref c1)) mask = Match(px, pal, c0, c1);
                }
                // four-colour mode needs c0 > c1; swapping the endpoints flips every index (0<->1, 2<->3)
                if (c0 < c1) { (c0, c1) = (c1, c0); mask ^= 0x55555555; }
                else if (c0 == c1) mask = 0;
                dst[o] = (byte)c0; dst[o + 1] = (byte)(c0 >> 8); dst[o + 2] = (byte)c1; dst[o + 3] = (byte)(c1 >> 8);
                dst[o + 4] = (byte)mask; dst[o + 5] = (byte)(mask >> 8); dst[o + 6] = (byte)(mask >> 16); dst[o + 7] = (byte)(mask >> 24);
                o += 8;
            }
    }

    private static ushort To565(int r, int g, int b)
        => (ushort)((((r * 31 + 127) / 255) << 11) | (((g * 63 + 127) / 255) << 5) | ((b * 31 + 127) / 255));

    private static void Palette(Span<int> pal, ushort c0, ushort c1)
    {
        int r0 = (c0 >> 11) & 31, g0 = (c0 >> 5) & 63, b0 = c0 & 31, r1 = (c1 >> 11) & 31, g1 = (c1 >> 5) & 63, b1 = c1 & 31;
        r0 = (r0 << 3) | (r0 >> 2); g0 = (g0 << 2) | (g0 >> 4); b0 = (b0 << 3) | (b0 >> 2);
        r1 = (r1 << 3) | (r1 >> 2); g1 = (g1 << 2) | (g1 >> 4); b1 = (b1 << 3) | (b1 >> 2);
        pal[0] = r0; pal[1] = g0; pal[2] = b0;
        pal[3] = r1; pal[4] = g1; pal[5] = b1;
        pal[6] = (2 * r0 + r1) / 3; pal[7] = (2 * g0 + g1) / 3; pal[8] = (2 * b0 + b1) / 3;
        pal[9] = (r0 + 2 * r1) / 3; pal[10] = (g0 + 2 * g1) / 3; pal[11] = (b0 + 2 * b1) / 3;
    }

    /// <summary>Nearest of the four palette colours for every pixel; 2 bits each, pixel 0 in the low bits.</summary>
    private static uint Match(Span<int> px, Span<int> pal, ushort c0, ushort c1)
    {
        Palette(pal, c0, c1);
        uint mask = 0;
        for (int i = 15; i >= 0; i--)
        {
            int r = px[i * 3], g = px[i * 3 + 1], b = px[i * 3 + 2];
            int best = 0, bestD = int.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                int dr = r - pal[k * 3], dg = g - pal[k * 3 + 1], db = b - pal[k * 3 + 2];
                int d = dr * dr * 2 + dg * dg * 4 + db * db; // rough luma weighting
                if (d < bestD) { bestD = d; best = k; }
            }
            mask = (mask << 2) | (uint)best;
        }
        return mask;
    }

    /// <summary>Least-squares endpoints for the current index assignment. False when nothing changed.</summary>
    private static bool Refine(Span<int> px, uint mask, ref ushort c0, ref ushort c1)
    {
        // weight of endpoint 0 (in thirds) per index: 0 -> 3, 1 -> 0, 2 -> 2, 3 -> 1
        ReadOnlySpan<int> w0 = [3, 0, 2, 1];
        int akku00 = 0, akku11 = 0, akku01 = 0;
        int r0 = 0, g0 = 0, b0 = 0, r1 = 0, g1 = 0, b1 = 0;
        uint m = mask;
        for (int i = 0; i < 16; i++, m >>= 2)
        {
            int a = w0[(int)(m & 3)], b = 3 - a;
            akku00 += a * a; akku11 += b * b; akku01 += a * b;
            int r = px[i * 3], g = px[i * 3 + 1], bl = px[i * 3 + 2];
            r0 += a * r; g0 += a * g; b0 += a * bl;
            r1 += b * r; g1 += b * g; b1 += b * bl;
        }
        float det = akku00 * akku11 - akku01 * akku01;
        if (MathF.Abs(det) < 1e-3f) return false;
        float f = 3f / det;
        static int Clamp8(float v) => v < 0 ? 0 : v > 255 ? 255 : (int)(v + 0.5f);
        var n0 = To565(Clamp8((r0 * akku11 - r1 * akku01) * f), Clamp8((g0 * akku11 - g1 * akku01) * f), Clamp8((b0 * akku11 - b1 * akku01) * f));
        var n1 = To565(Clamp8((r1 * akku00 - r0 * akku01) * f), Clamp8((g1 * akku00 - g0 * akku01) * f), Clamp8((b1 * akku00 - b0 * akku01) * f));
        if (n0 == c0 && n1 == c1) return false;
        c0 = n0; c1 = n1;
        return true;
    }

    /// <summary>Software decode, for checks only.</summary>
    public static void Decode(ReadOnlySpan<byte> src, int w, int h, Span<byte> bgra)
    {
        Span<int> pal = stackalloc int[12];
        int o = 0;
        for (int by = 0; by < h; by += 4)
            for (int bx = 0; bx < w; bx += 4, o += 8)
            {
                ushort c0 = (ushort)(src[o] | src[o + 1] << 8), c1 = (ushort)(src[o + 2] | src[o + 3] << 8);
                uint mask = (uint)(src[o + 4] | src[o + 5] << 8 | src[o + 6] << 16 | src[o + 7] << 24);
                Palette(pal, c0, c1);
                for (int i = 0; i < 16; i++, mask >>= 2)
                {
                    int k = (int)(mask & 3) * 3, p = ((by + i / 4) * w + bx + i % 4) * 4;
                    bgra[p] = (byte)pal[k + 2]; bgra[p + 1] = (byte)pal[k + 1]; bgra[p + 2] = (byte)pal[k]; bgra[p + 3] = 255;
                }
            }
    }
}

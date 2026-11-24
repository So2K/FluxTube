using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FluxTube;

if (args.Length > 0 && args[0] == "--selftest")
{
    // one block: left half blue, right half white, plus a single white pixel at (0,0)
    var blk = new byte[64];
    for (int i = 0; i < 16; i++) { bool white = i % 4 >= 2 || i == 0; blk[i * 4] = (byte)(white ? 255 : 200); blk[i * 4 + 1] = (byte)(white ? 255 : 80); blk[i * 4 + 2] = (byte)(white ? 255 : 20); blk[i * 4 + 3] = 255; }
    var enc = new byte[8]; Bc1.Encode(blk, 4, 4, enc);
    var dec2 = new byte[64]; Bc1.Decode(enc, 4, 4, dec2);
    Console.WriteLine("encoded: " + Convert.ToHexString(enc));
    for (int y = 0; y < 4; y++) Console.WriteLine(string.Join(" ", Enumerable.Range(0, 4).Select(x => $"{blk[(y * 4 + x) * 4 + 2]:X2}>{dec2[(y * 4 + x) * 4 + 2]:X2}")));
    return;
}
// args: output png, then source images. Left half of each row: original, right half: after BC1.
const int W = 480, H = 268;
var files = args.Skip(1).ToArray();
using var sheet = new Bitmap(W * 2 + 8, (H + 8) * files.Length);
using var g = Graphics.FromImage(sheet);
g.Clear(Color.Black);
double ms = 0, err = 0;
for (int n = 0; n < files.Length; n++)
{
    using var src = new Bitmap(Image.FromFile(files[n]));
    using var small = new Bitmap(W, H, PixelFormat.Format32bppArgb);
    using (var sg = Graphics.FromImage(small))
    {
        sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        int ch = src.Width * H / W;
        sg.DrawImage(src, new Rectangle(0, 0, W, H), new Rectangle(0, Math.Max(0, (src.Height - ch) / 2), src.Width, Math.Min(ch, src.Height)), GraphicsUnit.Pixel);
    }
    var bits = small.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    var px = new byte[W * H * 4];
    Marshal.Copy(bits.Scan0, px, 0, px.Length);
    var bc = new byte[Bc1.Size(W, H)];
    var sw = Stopwatch.StartNew();
    Bc1.Encode(px, W, H, bc);
    ms += sw.Elapsed.TotalMilliseconds;
    var back = new byte[px.Length];
    Bc1.Decode(bc, W, H, back);
    double se = 0;
    for (int i = 0; i < px.Length; i += 4) for (int c = 0; c < 3; c++) { double d = px[i + c] - back[i + c]; se += d * d; }
    err += 10 * Math.Log10(255.0 * 255.0 / (se / (W * H * 3)));
    small.UnlockBits(bits);
    g.DrawImage(small, 0, n * (H + 8));
    using var dec = new Bitmap(W, H, PixelFormat.Format32bppArgb);
    var db = dec.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    Marshal.Copy(back, 0, db.Scan0, back.Length);
    dec.UnlockBits(db);
    g.DrawImage(dec, W + 8, n * (H + 8));
}
sheet.Save(args[0], ImageFormat.Png);
Console.WriteLine($"{files.Length} thumbnails: encode {ms / files.Length:0.00} ms each, PSNR {err / files.Length:0.0} dB, {W * H * 4 / 1024} KB raw -> {Bc1.Size(W, H) / 1024} KB");

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Draws the Sortr icon (a white arrow dropping into a folder on a blue tile)
// at each standard size and packs the PNG frames into a .ico file.

var outDir = args.Length > 0 ? args[0] : Path.Combine("src", "Sortr.App", "Assets");
Directory.CreateDirectory(outDir);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var frames = sizes.Select(size => (size, png: RenderPng(size))).ToList();

WriteIco(Path.Combine(outDir, "Sortr.ico"), frames);
File.WriteAllBytes(Path.Combine(outDir, "icon-256.png"), frames.Last().png);
Console.WriteLine($"Wrote {frames.Count} frames to {Path.GetFullPath(outDir)}");

// Optional second argument: write a preview sheet of every size on light and dark backgrounds.
if (args.Length > 1)
{
    WritePreview(args[1], frames);
    Console.WriteLine($"Wrote preview to {Path.GetFullPath(args[1])}");
}

static void WritePreview(string path, IReadOnlyList<(int size, byte[] png)> frames)
{
    const int rowHeight = 300;
    using var sheet = new Bitmap(900, rowHeight * 2);
    using var g = Graphics.FromImage(sheet);
    g.InterpolationMode = InterpolationMode.NearestNeighbor;
    g.PixelOffsetMode = PixelOffsetMode.Half;
    g.Clear(Color.White);
    using (var dark = new SolidBrush(Color.FromArgb(32, 32, 32)))
        g.FillRectangle(dark, 0, rowHeight, sheet.Width, rowHeight);

    for (int row = 0; row < 2; row++)
    {
        int x = 16, y = row * rowHeight + 16;
        foreach (var (size, png) in frames)
        {
            using var img = Image.FromStream(new MemoryStream(png));
            g.DrawImage(img, x, y, size, size);
            if (size <= 32) // magnified copy underneath, to judge pixels
                g.DrawImage(img, x, y + 150, size * 4, size * 4);
            x += Math.Max(size, size <= 32 ? size * 4 : size) + 16;
        }
    }
    sheet.Save(path, ImageFormat.Png);
}

static byte[] RenderPng(int size)
{
    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 256f, size / 256f);
        Draw(g, small: size <= 24);
    }
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

// All coordinates are in a 256x256 design space.
static void Draw(Graphics g, bool small)
{
    // Tile: small sizes use the whole canvas so the shape doesn't turn into a blurry dot.
    var tile = small ? new RectangleF(0, 0, 256, 256) : new RectangleF(8, 8, 240, 240);
    using (var tilePath = RoundedRect(tile, small ? 44 : 56))
    using (var tileBrush = new LinearGradientBrush(tile, Color.FromArgb(0x3B, 0x82, 0xF6), Color.FromArgb(0x1D, 0x4E, 0xD8), 90f))
        g.FillPath(tileBrush, tilePath);

    // Folder back panel with its tab on the left, in mid blue so the white arrow and front panel stand out.
    float left = small ? 28 : 48, right = small ? 228 : 208;
    using (var backPath = new GraphicsPath { FillMode = FillMode.Winding })
    using (var backBrush = new SolidBrush(Color.FromArgb(0x93, 0xC5, 0xFD)))
    {
        backPath.AddPath(RoundedRect(new RectangleF(left, small ? 92 : 98, small ? 76 : 66, 40), 12), false);
        backPath.AddPath(RoundedRect(new RectangleF(left, small ? 112 : 118, right - left, 96), 14), false);
        g.FillPath(backBrush, backPath);
    }

    // Arrow dropping into the folder, right of the tab. It's drawn before the front panel so its tip
    // disappears "inside" the folder.
    using (var arrowBrush = new SolidBrush(Color.White))
    {
        float cx = small ? 156 : 150;
        float shaft = small ? 44 : 28;
        float head = small ? 56 : 38;
        float headBase = small ? 92 : 100;
        g.FillPath(arrowBrush, RoundedRect(new RectangleF(cx - shaft / 2, small ? 14 : 26, shaft, headBase - (small ? 4 : 16)), shaft / 2));
        g.FillPolygon(arrowBrush, [new PointF(cx - head, headBase), new PointF(cx + head, headBase), new PointF(cx, headBase + (small ? 58 : 52))]);
    }

    // Folder front panel.
    using (var frontBrush = new SolidBrush(Color.White))
        g.FillPath(frontBrush, RoundedRect(new RectangleF(left, small ? 138 : 142, right - left, small ? 90 : 78), 14));
}

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    float d = radius * 2;
    var path = new GraphicsPath();
    path.AddArc(r.X, r.Y, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

// ICO container with PNG-compressed frames (supported since Windows Vista).
static void WriteIco(string path, IReadOnlyList<(int size, byte[] png)> frames)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0);            // reserved
    w.Write((ushort)1);            // type: icon
    w.Write((ushort)frames.Count);

    int offset = 6 + 16 * frames.Count;
    foreach (var (size, png) in frames)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);          // palette colours
        w.Write((byte)0);          // reserved
        w.Write((ushort)1);        // colour planes
        w.Write((ushort)32);       // bits per pixel
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in frames)
        w.Write(png);
}

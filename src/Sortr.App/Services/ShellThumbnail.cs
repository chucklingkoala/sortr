using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sortr.App.Services;

/// <summary>
/// Gets the thumbnail Explorer would show for a file (a video frame, a PDF's first page, …), or the file-type icon
/// when the file has no thumbnail.
/// </summary>
public static class ShellThumbnail
{
    /// <summary>Returns a frozen bitmap no larger than <paramref name="size"/> pixels square, or null on failure.</summary>
    public static Task<BitmapSource?> GetAsync(string path, int size)
    {
        // Shell thumbnail providers are COM objects that expect an STA thread; keep them off the UI thread.
        var tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(Get(path, size));
            }
            catch
            {
                tcs.SetResult(null);
            }
        })
        { IsBackground = true, Name = "Sortr thumbnail" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static BitmapSource? Get(string path, int size)
    {
        // The shell parser rejects forward slashes; normalise to a plain absolute Windows path.
        path = System.IO.Path.GetFullPath(path);
        var iid = typeof(IShellItemImageFactory).GUID;
        int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        if (hr < 0 || factory is null)
            return null;
        try
        {
            // RESIZETOFIT returns the thumbnail when there is one and falls back to the icon otherwise.
            hr = factory.GetImage(new NativeSize { Width = size, Height = size }, SIIGBF_RESIZETOFIT, out var hBitmap);
            if (hr < 0 || hBitmap == IntPtr.Zero)
                return null;
            try
            {
                return ToBitmapSource(hBitmap);
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies a 32-bit HBITMAP into a WPF bitmap, keeping its alpha channel. (Imaging.CreateBitmapSourceFromHBitmap
    /// drops alpha, which turns transparent icon backgrounds black.)
    /// </summary>
    private static BitmapSource? ToBitmapSource(IntPtr hBitmap)
    {
        if (GetObject(hBitmap, Marshal.SizeOf<NativeBitmap>(), out var bmp) == 0 || bmp.Width <= 0 || bmp.Height <= 0)
            return null;

        int width = bmp.Width, height = bmp.Height, stride = width * 4;
        var pixels = new byte[stride * height];
        var info = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // negative = top-down rows
            Planes = 1,
            BitCount = 32,
        };

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hBitmap, 0, (uint)height, pixels, ref info, DIB_RGB_COLORS) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        // Thumbnails of opaque images often come with an all-zero alpha channel; treat those as opaque.
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        var format = hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        var source = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private const int SIIGBF_RESIZETOFIT = 0x0;
    private const uint DIB_RGB_COLORS = 0;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hBitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
}

using System.Drawing.Imaging;

namespace TitleOverlay;

/// <summary>
/// 把 32bpp 预乘位图交给分层窗口（UpdateLayeredWindow）。
/// 用 CreateDIBSection 手工填像素，避开 Bitmap.GetHbitmap 丢 alpha 的问题。
/// </summary>
internal static class LayeredSurface
{
    public static bool Present(IntPtr hwnd, Bitmap bitmap, Point position, int opacityPercent)
    {
        if (hwnd == IntPtr.Zero || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return false;
        }

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        IntPtr memDc = IntPtr.Zero;
        IntPtr hDib = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                return false;
            }

            var info = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = 40,
                    biWidth = bitmap.Width,
                    biHeight = -bitmap.Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            hDib = NativeMethods.CreateDIBSection(memDc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (hDib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return false;
            }

            CopyPixels(bitmap, bits);
            oldBitmap = NativeMethods.SelectObject(memDc, hDib);

            var size = new NativeMethods.SIZE(bitmap.Width, bitmap.Height);
            var src = new NativeMethods.POINT(0, 0);
            var dst = new NativeMethods.POINT(position.X, position.Y);
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = (byte)(Math.Clamp(opacityPercent, 1, 100) * 255 / 100),
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            return NativeMethods.UpdateLayeredWindow(
                hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memDc, oldBitmap);
            }
            if (hDib != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hDib);
            }
            if (memDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memDc);
            }
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static void CopyPixels(Bitmap bitmap, IntPtr destination)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int rowBytes = width * 4;

        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            if (data.Stride == rowBytes)
            {
                NativeMethods.CopyMemory(destination, data.Scan0, (IntPtr)((long)rowBytes * height));
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    var srcRow = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                    var dstRow = new IntPtr(destination.ToInt64() + (long)y * rowBytes);
                    NativeMethods.CopyMemory(dstRow, srcRow, (IntPtr)rowBytes);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public static Bitmap CreateSolid(int width, int height, Color color)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(color);
        return bmp;
    }
}

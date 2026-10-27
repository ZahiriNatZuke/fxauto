using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FxAuto.Ui.Settings;

/// <summary>The one source of FxAuto's icon: a filled circle with a white sound wave (two bars when paused). The tray
/// draws its 32 px state icons with <see cref="Draw"/>; the windows use <see cref="Window"/> and the .exe uses
/// <c>src/FxAuto/app.ico</c>, both built by <see cref="BuildIco"/> in the normal (verified, green) colour. Regenerate the
/// .ico with <c>tools/make-icon</c> after changing the drawing.</summary>
internal static class AppIcon
{
    public static readonly Color Verified = Color.FromArgb(46, 160, 67);
    public static readonly Color Pending = Color.FromArgb(219, 160, 30);
    public static readonly Color Locked = Color.FromArgb(47, 111, 214);
    public static readonly Color Paused = Color.FromArgb(130, 130, 130);
    public static readonly Color Failed = Color.FromArgb(210, 50, 45);

    /// <summary>Sizes written to the .ico and to <see cref="Window"/>.</summary>
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    private static Icon? _window;

    /// <summary>Shared multi-size icon for the settings window and its dialogs (lives as long as the process;
    /// WinForms derives and frees its own small/large copies per window).</summary>
    public static Icon Window => _window ??= new Icon(new MemoryStream(BuildIco(Verified, IcoSizes)));

    /// <summary>Draws the icon in a <paramref name="size"/>×<paramref name="size"/> square at the origin. 32 px is the
    /// original tray drawing; other sizes snap the bars to whole pixels so they stay sharp.</summary>
    public static void Draw(Graphics g, int size, Color fill, bool paused)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (size == 32)
        {
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 2, 2, 28, 28);
            using var pen = new Pen(Color.White, 3f);
            if (paused)
            {
                g.DrawLine(pen, 12, 10, 12, 22);
                g.DrawLine(pen, 20, 10, 20, 22);
            }
            else
            {
                g.DrawLine(pen, 9, 13, 9, 19);
                g.DrawLine(pen, 14, 9, 14, 23);
                g.DrawLine(pen, 19, 11, 19, 21);
                g.DrawLine(pen, 24, 14, 24, 18);
            }
            return;
        }

        float s = size / 32f;
        using (var brush = new SolidBrush(fill))
            g.FillEllipse(brush, 2 * s, 2 * s, 28 * s, 28 * s);

        g.SmoothingMode = SmoothingMode.None;   // bars on the pixel grid
        int w = Math.Max(2, R(3 * s));
        int[] halfHeights = paused ? [6, 6] : [3, 7, 5, 2];
        int pitch = paused ? Math.Max(w + 2, R(8 * s)) : Math.Max(w + 1, R(5 * s));
        int span = pitch * (halfHeights.Length - 1) + w;
        int left = R((size - span) / 2f);
        int mid = R(size / 2f);
        using var white = new SolidBrush(Color.White);
        for (int i = 0; i < halfHeights.Length; i++)
        {
            int hh = Math.Max(1, R(halfHeights[i] * s));
            g.FillRectangle(white, left + i * pitch, mid - hh, w, 2 * hh);
        }
    }

    private static int R(float v) => (int)MathF.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>Renders one icon state into a new 32bpp ARGB bitmap (caller disposes).</summary>
    public static Bitmap Render(int size, Color fill, bool paused)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        Draw(g, size, fill, paused);
        return bmp;
    }

    /// <summary>Builds a .ico file: 32bpp DIB entries below 256 px, PNG for 256 px.</summary>
    public static byte[] BuildIco(Color fill, IReadOnlyList<int> sizes)
    {
        var images = new List<byte[]>(sizes.Count);
        foreach (var size in sizes)
        {
            using var bmp = Render(size, fill, paused: false);
            images.Add(size >= 256 ? Png(bmp) : Dib(bmp));
        }

        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1);   // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)sizes.Count);
        ms.Write(header);
        int offset = 6 + 16 * sizes.Count;
        Span<byte> entry = stackalloc byte[16];
        for (int i = 0; i < sizes.Count; i++)
        {
            entry.Clear();
            entry[0] = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            entry[1] = entry[0];
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);    // planes
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);   // bit count
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..], images[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], offset);
            ms.Write(entry);
            offset += images[i].Length;
        }
        foreach (var img in images) ms.Write(img);
        return ms.ToArray();
    }

    private static byte[] Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    // BITMAPINFOHEADER + bottom-up BGRA pixels + 1bpp AND mask (all zero: alpha carries the transparency).
    private static byte[] Dib(Bitmap bmp)
    {
        int n = bmp.Width;
        int maskStride = (n + 31) / 32 * 4;
        var data = new byte[40 + n * n * 4 + maskStride * n];
        var span = data.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, 40);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], n);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], n * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], n * n * 4 + maskStride * n);

        var bits = bmp.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[n * 4];
            for (int y = 0; y < n; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + (n - 1 - y) * bits.Stride, row, 0, row.Length);
                row.CopyTo(span[(40 + y * n * 4)..]);
            }
        }
        finally { bmp.UnlockBits(bits); }
        return data;
    }
}

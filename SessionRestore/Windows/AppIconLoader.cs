using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using static DisplayLullaby.SessionRestore.SessionNative;

namespace DisplayLullaby.SessionRestore;

/// <summary>App icons for the settings list: Store package logo first, else the exe's shell icon.</summary>
internal static unsafe class AppIconLoader
{
    private const int Size = 32;

    public static Bitmap? Load(AppRow row)
    {
        if (!OperatingSystem.IsWindows() || row.ExePath is not { } exe)
        {
            return null;
        }

        try
        {
            if (row.AppUserModelId is not null &&
                PackageLogo.Find(exe, File.Exists, ReadTextOrNull, (dir, pattern) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern) : []) is { } logo)
            {
                return new Bitmap(logo);
            }

            return File.Exists(exe) ? FromShellIcon(exe) : null;
        }
        catch (Exception)
        {
            return null; // icon is cosmetic; the row still shows name and titles
        }
    }

    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Renders the HICON into a 32-bit top-down DIB and copies the premultiplied BGRA pixels.</summary>
    private static Bitmap? FromShellIcon(string path)
    {
        var info = new ShFileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)sizeof(ShFileInfo), ShgfiIcon | ShgfiLargeIcon) == 0 || info.Icon == 0)
        {
            return null;
        }

        var dc = CreateCompatibleDC(0);
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = Size,
            Height = -Size,
            Planes = 1,
            BitCount = 32
        };
        var dib = CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
        try
        {
            if (dib == 0 || bits == null)
            {
                return null;
            }

            var old = SelectObject(dc, dib);
            DrawIconEx(dc, 0, 0, info.Icon, Size, Size, 0, 0, DiNormal);
            SelectObject(dc, old);

            var pixels = new Span<uint>(bits, Size * Size);
            var anyAlpha = false;
            foreach (var p in pixels)
            {
                if ((p & 0xFF000000) != 0)
                {
                    anyAlpha = true;
                    break;
                }
            }

            if (!anyAlpha)
            {
                // Legacy icons without an alpha channel: treat drawn pixels as opaque.
                for (var i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i] != 0)
                    {
                        pixels[i] |= 0xFF000000;
                    }
                }
            }

            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (nint)bits, new PixelSize(Size, Size), new Vector(96, 96), Size * 4);
        }
        finally
        {
            if (dib != 0)
            {
                DeleteObject(dib);
            }

            DeleteDC(dc);
            DestroyIcon(info.Icon);
        }
    }
}

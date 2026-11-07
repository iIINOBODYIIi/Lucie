using System.Drawing;
using System.Drawing.Imaging;
using Lucie.Native;

namespace Lucie.Services;

/// <summary>Capture tous les écrans et enregistre l'image dans Images\Lucie.</summary>
public static class ScreenCapture
{
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public static string CaptureToPictures()
    {
        int x = Win32.GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = Win32.GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = Win32.GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = Win32.GetSystemMetrics(SM_CYVIRTUALSCREEN);

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Lucie");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"Capture_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");

        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        bitmap.Save(file, ImageFormat.Png);

        return file;
    }
}

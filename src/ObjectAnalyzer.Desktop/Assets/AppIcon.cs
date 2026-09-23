using System.IO;
using System.Windows.Media.Imaging;

namespace ObjectAnalyzer.Desktop.Assets;

internal static class AppIcon
{
    public static BitmapFrame? Load()
    {
        try
        {
            using var stream = new MemoryStream(AppIconData.GetIcoBytes());
            return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Write app.ico next to the assembly for published layouts.</summary>
    public static void EnsureFileBesideAssembly()
    {
        try
        {
            string? dir = Path.GetDirectoryName(typeof(AppIcon).Assembly.Location);
            if (string.IsNullOrEmpty(dir))
                return;

            string path = Path.Combine(dir, "app.ico");
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return;

            File.WriteAllBytes(path, AppIconData.GetIcoBytes());
        }
        catch
        {
            // Icon file is best-effort.
        }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskSpaceGuard.Controls;
using DiskSpaceGuard.Models;
using DiskSpaceGuard.Services;

namespace DiskSpaceGuard.ScanTests;

/// <summary>UI 验证工具：扫描指定目录并把树图的三种配色渲染为 PNG。</summary>
public static class PreviewRenderer
{
    public static async Task RunAsync(string source, string outputDir)
    {
        var result = await new ScanService().ScanAsync(source, ScanMode.FullRecursive, 1);
        Directory.CreateDirectory(outputDir);
        Write(result.Root, Path.Combine(outputDir, "type.png"), TreemapColorMode.Type, false);
        Write(result.Root, Path.Combine(outputDir, "depth.png"), TreemapColorMode.Depth, false);
        Write(result.Root, Path.Combine(outputDir, "merge.png"), TreemapColorMode.Type, true);
    }

    private static void Write(ScanNode root, string path, TreemapColorMode mode, bool mergeSmall)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new TreemapView
                {
                    Root = root,
                    ColorMode = mode,
                    MergeSmall = mergeSmall
                };
                var host = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x11, 0x17, 0x22)),
                    Child = view
                };
                var size = new Size(1200, 620);
                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                var bitmap = new RenderTargetBitmap(1200, 620, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(path);
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
    }
}

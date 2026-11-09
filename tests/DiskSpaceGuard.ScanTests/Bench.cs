using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskSpaceGuard.Controls;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.ScanTests;

/// <summary>
/// T14：在 500 / 1500 / 5000 节点下测量布局 + 渲染耗时（含 2.2 万节点参考）。
/// 在 STA 线程上用 RenderTargetBitmap 触发真实 OnRender，而不是只测算法。
/// </summary>
public static class Bench
{
    private const double Width = 1200;
    private const double Height = 520;

    public static void Run()
    {
        var thread = new Thread(RunCore);
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static void RunCore()
    {
        Console.WriteLine("视图 | 节点数 | 渲染块 | 布局+渲染(ms) | 备注");
        Console.WriteLine(new string('-', 62));

        foreach (var count in new[] { 500, 1500, 5000, 22000 })
        {
            var flat = BuildTree(count, nested: false);
            var tree = new TreemapView { Root = flat };
            var treeMs = Measure(tree);
            var blocks = CountBoxes(tree);
            Console.WriteLine($"树图 | {count:N0} | {blocks:N0} | {treeMs:0.0} | {(tree.Truncated ? "已折叠" : "完整")}");

            var nested = BuildTree(count, nested: true);
            var nestedView = new TreemapView { Root = nested };
            var nestedMs = Measure(nestedView);
            var nestedBlocks = CountBoxes(nestedView);
            Console.WriteLine($"树图(嵌套) | {count:N0} | {nestedBlocks:N0} | {nestedMs:0.0} | {(nestedView.Truncated ? "已折叠" : "完整")}");

            var sun = new SunburstView { Root = BuildTree(count, nested: false) };
            Console.WriteLine($"旭日 | {count:N0} | - | {Measure(sun):0.0} | -");

            var bars = new BarsView { Root = BuildTree(count, nested: false) };
            Console.WriteLine($"条形 | {count:N0} | - | {Measure(bars):0.0} | -");
            Console.WriteLine();
        }

        // 20 次连续重绘（T16 初探）：观察是否出现耗时增长
        var loopRoot = BuildTree(22000, nested: true);
        var loopView = new TreemapView { Root = loopRoot };
        var first = Measure(loopView, warmup: true);
        double last = 0;
        for (var i = 0; i < 20; i++)
        {
            loopView.InvalidateVisual();
            last = Measure(loopView, warmup: true);
        }
        Console.WriteLine($"连续重绘 20 次：首次 {first:0.0} ms，末次 {last:0.0} ms（22,000 节点嵌套）");
    }

    private static ScanNode BuildTree(int nodeCount, bool nested)
    {
        var root = new ScanNode("bench", @"C:\bench", NodeKind.Directory);

        if (!nested)
        {
            for (var i = 0; i < nodeCount; i++)
                root.AddChild(File($"file-{i:00000}.bin", 1024L * (i % 97 + 1)));
            root.LogicalSize = nodeCount * 1024L * 48;
            return root;
        }

        const int folders = 28;
        var perFolder = Math.Max(1, nodeCount / folders);
        for (var d = 0; d < folders; d++)
        {
            var dir = new ScanNode($"dir-{d:00}", $@"C:\bench\dir-{d:00}", NodeKind.Directory)
            {
                LastWriteUtc = DateTime.UtcNow
            };
            for (var i = 0; i < perFolder; i++)
                dir.AddChild(File($"f-{i:0000}.dat", 4096L * (i % 13 + 1)));
            dir.LogicalSize = perFolder * 4096L * 7;
            root.AddChild(dir);
        }
        root.LogicalSize = folders * perFolder * 4096L * 7;
        return root;
    }

    private static ScanNode File(string name, long size) =>
        new(name, $@"C:\bench\{name}", NodeKind.File) { LogicalSize = size };

    /// <summary>取三次中最小耗时，避免 GC 抖动影响。</summary>
    private static double Measure(FrameworkElement view, bool warmup = false)
    {
        var best = double.MaxValue;
        var runs = warmup ? 1 : 3;
        for (var i = 0; i < runs; i++)
        {
            view.InvalidateMeasure();
            view.InvalidateVisual();
            var sw = Stopwatch.StartNew();
            view.Measure(new Size(Width, Height));
            view.Arrange(new Rect(0, 0, Width, Height));
            view.UpdateLayout();

            var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            sw.Stop();

            if (sw.Elapsed.TotalMilliseconds < best) best = sw.Elapsed.TotalMilliseconds;
        }
        return best;
    }

    private static int CountBoxes(TreemapView view)
    {
        var field = typeof(TreemapView).GetField("_boxes", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(view) is System.Collections.ICollection list ? list.Count : -1;
    }
}

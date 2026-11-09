using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Controls;

public enum TreemapColorMode
{
    Type,
    Depth,
    Size
}

internal static class VisualColors
{
    private static readonly Color[] Categories =
    {
        Color.FromRgb(0x5B, 0x8D, 0xEF), // System
        Color.FromRgb(0x8A, 0x6E, 0xE6), // Program
        Color.FromRgb(0xE8, 0xA3, 0x3D), // Media
        Color.FromRgb(0x4C, 0xC3, 0x8A), // Document
        Color.FromRgb(0xD9, 0x6A, 0x8A), // Archive
        Color.FromRgb(0x98, 0xA0, 0xAB)  // Other
    };

    private static readonly double[] Hues = { 218, 152, 34, 280, 190, 6 };

    private static readonly Brush DarkTextBrush = Freeze(new SolidColorBrush(Colors.Black));
    private static readonly Brush LightTextBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush FallbackMutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5C, 0x74, 0x80)));

    /// <summary>
    /// 空态 / 辅助说明文字。优先取 SecondaryTextBrush（卡片上 4.92:1），
    /// 使图表文字与 XAML 层共用同一层级；无 Application（如渲染测试）时回退到同值。
    /// </summary>
    public static Brush MutedText =>
        Application.Current?.TryFindResource("SecondaryTextBrush") as Brush ?? FallbackMutedBrush;

    /// <summary>
    /// 按底色在深墨与白字之间取对比度更高者。
    /// 深墨必须纯黑：若用 #121822（L≈0.0075），黑白交叉点会从 L=0.179 漂到 L≈0.196，
    /// 在 0.18–0.20 区间两者都掉到 4.0 左右（Size 模式实测 3.93:1）。
    /// 纯黑下交叉点恒为 4.58:1，因此全色域均可 ≥4.5。
    /// </summary>
    public static Brush TextBrushFor(Color color) =>
        Contrast(color, Colors.Black) >= Contrast(color, Colors.White) ? DarkTextBrush : LightTextBrush;

    /// <summary>WCAG 对比度。</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>相对亮度（WCAG）：用于在深浅底色上自动选择可读的文字/描边颜色。</summary>
    public static double RelativeLuminance(Color color)
    {
        static double Linear(byte value)
        {
            var s = value / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static Color For(ScanNode node, TreemapColorMode mode, int depth, long maxSize)
    {
        if (mode == TreemapColorMode.Type)
        {
            var baseColor = Categories[(int)node.Category];
            // 父级用较深的同色相，子级逐层提亮，避免整张图靠标题条才看得出层级。
            return depth <= 0 ? baseColor : Lighten(baseColor, Math.Min(0.18, depth * 0.06));
        }

        if (mode == TreemapColorMode.Depth)
            return FromHsl(Hues[Math.Min(depth, Hues.Length - 1)], 0.50, Math.Max(0.38, 0.66 - depth * 0.05));

        var ratio = maxSize <= 0 ? 1 : Math.Clamp((double)node.LogicalSize / maxSize, 0, 1);
        return FromHsl(218, 0.56, 0.72 - 0.34 * ratio);
    }

    /// <summary>向白色混合，保持色相不变；用于按层级提亮。</summary>
    public static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)(color.R + (255 - color.R) * amount),
        (byte)(color.G + (255 - color.G) * amount),
        (byte)(color.B + (255 - color.B) * amount));

    /// <summary>压暗同色相，用于目录标题条，使其成为该区块的视觉锚点。</summary>
    public static Color Darken(Color color, double factor) => Color.FromRgb(
        (byte)(color.R * factor),
        (byte)(color.G * factor),
        (byte)(color.B * factor));

    private static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

/// <summary>
/// 文件级嵌套树图：目录块内嵌子层，一直绘制到文件；超过块数预算时停止内嵌。
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    private sealed class Box
    {
        public required ScanNode Node { get; init; }
        public Rect Rect { get; init; }
        public bool IsHeader { get; init; }
        public bool IsContainer { get; init; }
        public int Depth { get; init; }
    }

    /// <summary>黄金分割比例 1/φ ≈ 0.6180339887。</summary>
    private const double GoldenSection = 0.6180339887498949;
    /// <summary>φ² ≈ 2.618，作为单块可接受的长宽比上限。</summary>
    private const double AspectLimit = 2.618033988749895;

    private static readonly Pen LeafSeparatorPen = CreateLeafSeparatorPen();

    private static Pen CreateLeafSeparatorPen()
    {
        var brush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));
        brush.Freeze();
        var pen = new Pen(brush, 1);
        pen.Freeze();
        return pen;
    }

    private readonly List<Box> _boxes = new();
    /// <summary>悬停描边画在独立子视觉上，避免鼠标移动触发整张树图重排重绘。</summary>
    private readonly DrawingVisual _hoverLayer = new();
    private ScanNode? _root;
    private Box? _hoverBox;
    private TreemapColorMode _colorMode = TreemapColorMode.Type;
    private bool _mergeSmall;
    private int _maxDepth = 7;
    private int _maxBlocks = 1500;

    public TreemapView()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        AddVisualChild(_hoverLayer);
    }

    public ScanNode? Root
    {
        get => _root;
        set
        {
            _root = value;
            ResetVisual();
        }
    }

    public TreemapColorMode ColorMode
    {
        get => _colorMode;
        set
        {
            if (_colorMode == value) return;
            _colorMode = value;
            ResetVisual();
        }
    }

    public bool MergeSmall
    {
        get => _mergeSmall;
        set
        {
            if (_mergeSmall == value) return;
            _mergeSmall = value;
            ResetVisual();
        }
    }

    public int MaxDepth
    {
        get => _maxDepth;
        set
        {
            if (_maxDepth == value) return;
            _maxDepth = value;
            ResetVisual();
        }
    }

    public int MaxBlocks
    {
        get => _maxBlocks;
        set
        {
            if (_maxBlocks == value) return;
            _maxBlocks = value;
            ResetVisual();
        }
    }

    public bool Truncated { get; private set; }

    private void ResetVisual()
    {
        _hoverBox = null;
        using (_hoverLayer.RenderOpen()) { }
        InvalidateVisual();
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _hoverLayer : throw new ArgumentOutOfRangeException(nameof(index));

    public event Action<ScanNode>? DirectoryActivated;
    public event Action<ScanNode>? FileActivated;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        _boxes.Clear();
        Truncated = false;

        if (_root is null || _root.Children.Count == 0 || ActualWidth < 4 || ActualHeight < 4)
        {
            DrawCenterText(dc, "无子项");
            return;
        }

        var kids = Prepare(_root.Children);
        if (kids.Count == 0)
        {
            DrawCenterText(dc, "无子项");
            return;
        }

        Layout(kids, new Rect(0, 0, ActualWidth, ActualHeight), 0);
        foreach (var box in _boxes) DrawBox(dc, box);
        if (Truncated) DrawTruncationHint(dc);
    }

    private void DrawTruncationHint(DrawingContext dc)
    {
        var text = MakeText($"已超过 {_maxBlocks} 块，深层已折叠", 10, Brushes.White);
        var rect = new Rect(ActualWidth - text.Width - 16, ActualHeight - text.Height - 10, text.Width + 10, text.Height + 4);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null, rect, 4, 4);
        dc.DrawText(text, new Point(rect.X + 5, rect.Y + 2));
    }

    private List<ScanNode> Prepare(IReadOnlyList<ScanNode> children)
    {
        var list = children.Where(c => c.LogicalSize > 0).OrderByDescending(c => c.LogicalSize).ToList();
        if (!MergeSmall) return CapLevel(list);

        var kept = new List<ScanNode>();
        long sum = 0;
        var count = 0;
        foreach (var child in list)
        {
            if (child.Kind == NodeKind.File && !child.IsAggregate && child.LogicalSize < 1024 * 1024)
            {
                sum += child.LogicalSize;
                count++;
            }
            else kept.Add(child);
        }

        if (count > 0)
        {
            kept.Add(new ScanNode($"{count:N0} 项小于 1 MB", string.Empty, NodeKind.File)
            {
                LogicalSize = sum,
                IsAggregate = true,
                AggregatedCount = count
            });
        }
        return CapLevel(kept.OrderByDescending(c => c.LogicalSize).ToList());
    }

    /// <summary>单层最多占满全局块预算；超出部分合并为一个「其余 N 项」，避免画出上万个小方块。</summary>
    private List<ScanNode> CapLevel(List<ScanNode> list)
    {
        if (list.Count <= MaxBlocks) return list;

        var keep = list.Take(Math.Max(1, MaxBlocks - 1)).ToList();
        var tail = list.Skip(keep.Count).ToList();
        keep.Add(new ScanNode($"其余 {tail.Count:N0} 项", string.Empty, NodeKind.File)
        {
            LogicalSize = tail.Sum(t => Math.Max(1, t.LogicalSize)),
            IsAggregate = true,
            AggregatedCount = tail.Count
        });
        return keep.OrderByDescending(c => c.LogicalSize).ToList();
    }

    private void Layout(List<ScanNode> kids, Rect rect, int depth)
    {
        if (rect.Width < 2 || rect.Height < 2) return;

        foreach (var (node, area) in GoldenLayout(kids, rect))
        {
            if (_boxes.Count >= MaxBlocks)
            {
                Truncated = true;
                return;
            }

            var canNest = node.Kind == NodeKind.Directory && node.IsScanned && !node.IsAggregate
                && node.Children.Count > 0 && area.Width > 42 && area.Height > 30 && depth < MaxDepth;

            if (canNest)
            {
                var header = area.Height > 64 ? 17.0 : 13.0;
                _boxes.Add(new Box { Node = node, Rect = area, IsContainer = true, Depth = depth });
                _boxes.Add(new Box
                {
                    Node = node,
                    Rect = new Rect(area.X, area.Y, area.Width, header),
                    IsHeader = true,
                    IsContainer = true,
                    Depth = depth
                });

                var inner = new Rect(area.X + 1.5, area.Y + header + 1,
                    Math.Max(0, area.Width - 3), Math.Max(0, area.Height - header - 2));
                if (inner.Width > 4 && inner.Height > 4)
                    Layout(Prepare(node.Children), inner, depth + 1);
                continue;
            }

            _boxes.Add(new Box { Node = node, Rect = area, Depth = depth });
        }
    }

    private void DrawBox(DrawingContext dc, Box box)
    {
        var color = VisualColors.For(box.Node, ColorMode, box.Depth, Math.Max(1, _root?.LogicalSize ?? 1));
        var rect = new Rect(box.Rect.X + 0.8, box.Rect.Y + 0.8,
            Math.Max(0, box.Rect.Width - 1.6), Math.Max(0, box.Rect.Height - 1.6));
        if (rect.Width <= 0 || rect.Height <= 0) return;

        if (box.IsContainer && !box.IsHeader)
        {
            var fill = new SolidColorBrush(Color.FromArgb(46, color.R, color.G, color.B));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(36, 0, 0, 0)), 1);
            dc.DrawRoundedRectangle(fill, pen, rect, 3, 3);
            return;
        }

        if (box.IsContainer)
        {
            var headerColor = VisualColors.Darken(color, 0.78);
            dc.DrawRoundedRectangle(new SolidColorBrush(headerColor), null, rect, 3, 3);
            var label = $"{box.Node.Name}  {ScanNode.FormatBytes(box.Node.LogicalSize)}";
            DrawTextAt(dc, label, rect, 4, 10.5, 2, VisualColors.TextBrushFor(headerColor), FontWeights.SemiBold);
            return;
        }

        var leafBrush = new SolidColorBrush(color)
        {
            Opacity = box.Node.IsAggregate ? 0.55 : 0.92
        };
        // 过小的块不加描边，避免高密度下变成白噪点。
        var separator = rect.Width > 12 && rect.Height > 12 ? LeafSeparatorPen : null;
        dc.DrawRoundedRectangle(leafBrush, separator, rect, 2.5, 2.5);
        if (rect.Width > 40 && rect.Height > 16)
        {
            // 合并块以 55% 不透明度绘制，实际呈现色是 color 与背景（近白）的混合色，
            // 因此必须按混色后的有效色判断文字颜色，不能沿用白字（原实现 1.56–1.88:1）。
            var textBrush = box.Node.IsAggregate
                ? VisualColors.TextBrushFor(VisualColors.Lighten(color, 0.45))
                : VisualColors.TextBrushFor(color);
            DrawTextAt(dc, box.Node.Name, rect, 4, 10, 3, textBrush);
            if (rect.Width > 58 && rect.Height > 33)
                DrawTextAt(dc, ScanNode.FormatBytes(box.Node.LogicalSize), rect, 4, 9, 19, textBrush);
        }
    }

    /// <summary>悬停描边：浅色块用深色轮廓，深色块用白色轮廓，保证任何配色模式下都可见。</summary>
    private void RenderHover()
    {
        using var dc = _hoverLayer.RenderOpen();
        if (_hoverBox is null || !_boxes.Contains(_hoverBox)) return;

        var source = _hoverBox.Rect;
        var outline = new Rect(source.X + 0.9, source.Y + 0.9,
            Math.Max(0, source.Width - 1.8), Math.Max(0, source.Height - 1.8));
        if (outline.Width <= 0 || outline.Height <= 0) return;

        var color = VisualColors.For(_hoverBox.Node, ColorMode, _hoverBox.Depth, Math.Max(1, _root?.LogicalSize ?? 1));
        if (_hoverBox.IsHeader) color = VisualColors.Darken(color, 0.78);
        var brush = VisualColors.TextBrushFor(color);
        dc.DrawRoundedRectangle(null, new Pen(brush, 1.6), outline, 3, 3);
    }

    private void DrawTextAt(DrawingContext dc, string text, Rect rect, double left, double fontSize, double top, Brush brush,
        FontWeight? weight = null)
    {
        if (rect.Width - left * 2 <= 4) return;
        var formatted = MakeText(text, fontSize, brush, weight);
        formatted.MaxTextWidth = Math.Max(1, rect.Width - left * 2);
        formatted.MaxLineCount = 1;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        var y = top < 0 ? rect.Y + Math.Max(1, (rect.Height - formatted.Height) / 2) : rect.Y + top;
        dc.DrawText(formatted, new Point(rect.X + left, y));
    }

    private FormattedText MakeText(string text, double size, Brush brush, FontWeight? weight = null) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Microsoft YaHei, Segoe UI"), FontStyles.Normal,
            weight ?? FontWeights.Normal, FontStretches.Normal),
        size,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawCenterText(DrawingContext dc, string text)
    {
        var formatted = MakeText(text, 12, VisualColors.MutedText);
        dc.DrawText(formatted, new Point(Math.Max(0, (ActualWidth - formatted.Width) / 2),
            Math.Max(0, (ActualHeight - formatted.Height) / 2)));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var box = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(box, _hoverBox))
        {
            _hoverBox = box;
            RenderHover();
            var node = box?.Node;
            ToolTip = node is null
                ? null
                : $"{node.Name}\n{(string.IsNullOrEmpty(node.FullPath) ? "（合并项）" : node.FullPath)}\n{node.SizeDisplay}";
            Cursor = box is null ? Cursors.Arrow : Cursors.Hand;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverBox is not null)
        {
            _hoverBox = null;
            RenderHover();
        }
        ToolTip = null;
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var box = HitTest(e.GetPosition(this));
        if (box is null) return;

        if (box.Node.IsAggregate || box.Node.Kind != NodeKind.Directory || !box.Node.IsScanned)
        {
            FileActivated?.Invoke(box.Node);
            return;
        }

        if (box.IsContainer || box.Node.Children.Count > 0)
            DirectoryActivated?.Invoke(box.Node);
        else
            FileActivated?.Invoke(box.Node);
    }

    private Box? HitTest(Point point)
    {
        for (var i = _boxes.Count - 1; i >= 0; i--)
        {
            if (_boxes[i].Rect.Contains(point)) return _boxes[i];
        }
        return null;
    }

    /// <summary>
    /// 黄金分割布局：把一组子项按面积份额最接近 61.8% 的位置切成两组，并沿矩形长边
    /// 按真实面积份额下刀，因此每块面积仍与文件大小严格成正比。
    /// 若黄金切点会产生过于狭长的块，则改选长宽比最优的切点。
    /// </summary>
    internal static List<(ScanNode Node, Rect Rect)> GoldenLayout(IReadOnlyList<ScanNode> items, Rect rect)
    {
        var result = new List<(ScanNode, Rect)>(items.Count);
        if (items.Count == 0 || rect.Width <= 0 || rect.Height <= 0) return result;

        var pending = new Stack<(List<ScanNode> Group, double Total, Rect Area)>();
        pending.Push((items.ToList(), items.Sum(Weight), rect));

        while (pending.Count > 0)
        {
            var (group, totalSize, area) = pending.Pop();
            if (group.Count == 0) continue;
            if (group.Count == 1)
            {
                result.Add((group[0], area));
                continue;
            }

            var (pivot, firstSize) = ChoosePivot(group, totalSize, area);
            var share = firstSize / totalSize;

            Rect headRect;
            Rect tailRect;
            if (area.Width >= area.Height)
            {
                var width = area.Width * share;
                headRect = new Rect(area.X, area.Y, width, area.Height);
                tailRect = new Rect(area.X + width, area.Y, Math.Max(0, area.Width - width), area.Height);
            }
            else
            {
                var height = area.Height * share;
                headRect = new Rect(area.X, area.Y, area.Width, height);
                tailRect = new Rect(area.X, area.Y + height, area.Width, Math.Max(0, area.Height - height));
            }

            var head = group.GetRange(0, pivot);
            var tail = group.GetRange(pivot, group.Count - pivot);
            pending.Push((tail, totalSize - firstSize, tailRect));
            pending.Push((head, firstSize, headRect));
        }
        return result;
    }

    private static double Weight(ScanNode node) => Math.Max(1.0, node.LogicalSize);

    /// <summary>
    /// 优先选择面积份额最接近黄金分割的切点；候选切点须使两块的长宽比不超过 φ²。
    /// 若没有切点满足，则取长宽比最优者。
    /// </summary>
    private static (int Pivot, double FirstSize) ChoosePivot(List<ScanNode> group, double totalSize, Rect area)
    {
        var cumulative = 0.0;
        var goldenPivot = -1;
        var goldenSize = 0.0;
        var goldenDiff = double.MaxValue;

        var fallbackPivot = 1;
        var fallbackSize = 0.0;
        var fallbackAspect = double.MaxValue;
        var fallbackDiff = double.MaxValue;

        for (var k = 1; k < group.Count; k++)
        {
            cumulative += Weight(group[k - 1]);
            var share = cumulative / totalSize;
            var diff = Math.Abs(share - GoldenSection);
            var aspect = WorstAspect(share, area);

            if (aspect <= AspectLimit && diff < goldenDiff)
            {
                goldenDiff = diff;
                goldenPivot = k;
                goldenSize = cumulative;
            }

            if (aspect < fallbackAspect - 1e-9 ||
                (Math.Abs(aspect - fallbackAspect) <= 1e-9 && diff < fallbackDiff))
            {
                fallbackAspect = aspect;
                fallbackDiff = diff;
                fallbackPivot = k;
                fallbackSize = cumulative;
            }
        }

        return goldenPivot > 0 ? (goldenPivot, goldenSize) : (fallbackPivot, fallbackSize);
    }

    /// <summary>沿长边切分时，两块中较差的长宽比（≥1）。</summary>
    private static double WorstAspect(double share, Rect area)
    {
        var longSide = Math.Max(area.Width, area.Height);
        var shortSide = Math.Min(area.Width, area.Height);
        if (longSide <= 0 || shortSide <= 0 || share <= 0 || share >= 1) return double.MaxValue;

        var ratio = longSide / shortSide;
        var first = share * ratio;
        var second = (1 - share) * ratio;
        return Math.Max(Math.Max(first, 1 / first), Math.Max(second, 1 / second));
    }
}

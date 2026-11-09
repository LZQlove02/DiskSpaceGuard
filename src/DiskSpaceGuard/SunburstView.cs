using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskSpaceGuard.Models;

namespace DiskSpaceGuard.Controls;

/// <summary>旭日图：最多绘制 3 层扇区，点击下钻。</summary>
public sealed class SunburstView : FrameworkElement
{
    private sealed class Arc
    {
        public required ScanNode Node { get; init; }
        public required PathGeometry Geometry { get; init; }
    }

    private readonly List<Arc> _arcs = new();
    private ScanNode? _root;

    public SunburstView()
    {
        ClipToBounds = true;
    }

    public ScanNode? Root
    {
        get => _root;
        set { _root = value; InvalidateVisual(); }
    }

    public TreemapColorMode ColorMode { get; set; } = TreemapColorMode.Type;
    public int MaxLevels { get; set; } = 3;

    public event Action<ScanNode>? DirectoryActivated;
    public event Action<ScanNode>? FileActivated;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _arcs.Clear();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_root is null || _root.Children.Count == 0)
        {
            DrawCenter(dc, "无子项", string.Empty);
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var outer = Math.Max(30, Math.Min(ActualWidth, ActualHeight) / 2 - 10);
        var inner = Math.Max(26, outer * 0.30);
        var band = (outer - inner) / MaxLevels;

        DrawArcRing(dc, center, inner, inner + band, _root.Children, 0, Math.Max(1, _root.LogicalSize));

        var centerText = _root.LogicalSize > 0 ? ScanNode.FormatBytes(_root.LogicalSize) : "—";
        DrawCenter(dc, Trim(_root.Name, 14), centerText);
    }

    private void DrawArcRing(DrawingContext dc, Point center, double r0, double r1, IReadOnlyList<ScanNode> kids, int level, long maxSize)
    {
        var visible = kids.Where(k => k.LogicalSize > 0).OrderByDescending(k => k.LogicalSize).ToList();
        if (visible.Count == 0) return;

        var total = visible.Sum(k => (double)k.LogicalSize);
        if (total <= 0) return;

        var angle = -Math.PI / 2;
        const double gap = 0.006;

        foreach (var node in visible)
        {
            var sweep = node.LogicalSize / total * Math.PI * 2;
            if (sweep < 0.02) { angle += sweep; continue; }

            var start = angle + gap / 2;
            var end = angle + sweep - gap / 2;
            angle += sweep;
            if (end <= start) continue;

            var geometry = BuildArc(center, r0, r1, start, end);
            var color = VisualColors.For(node, ColorMode, level, maxSize);
            dc.DrawGeometry(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1), geometry);
            _arcs.Add(new Arc { Node = node, Geometry = geometry });

            if (sweep > 0.16 && r1 - r0 > 26)
            {
                var mid = (start + end) / 2;
                var radius = (r0 + r1) / 2;
                var point = new Point(center.X + radius * Math.Cos(mid), center.Y + radius * Math.Sin(mid));
                var text = MakeText(Trim(node.Name, 8), 10, Brushes.White);
                text.MaxTextWidth = Math.Max(20, r1 - r0 - 6);
                dc.DrawText(text, new Point(point.X - text.Width / 2, point.Y - text.Height / 2));
            }

            if (level + 1 < MaxLevels && node.Kind == NodeKind.Directory && node.Children.Count > 0 && sweep > 0.05)
                DrawArcRing(dc, center, r1 + 1, r1 + (r1 - r0), node.Children, level + 1, Math.Max(1, node.LogicalSize));
        }
    }

    private static PathGeometry BuildArc(Point center, double r0, double r1, double start, double end)
    {
        var p1 = PointAt(center, r1, start);
        var p2 = PointAt(center, r1, end);
        var p3 = PointAt(center, r0, end);
        var p4 = PointAt(center, r0, start);
        var large = end - start > Math.PI ? 1 : 0;

        var figure = new PathFigure { StartPoint = p1, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(p2, new Size(r1, r1), 0, large == 1, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(p3, true));
        figure.Segments.Add(new ArcSegment(p4, new Size(r0, r0), 0, large == 1, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void DrawCenter(DrawingContext dc, string title, string subtitle)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var t = MakeText(Trim(title, 14), 12.5, new SolidColorBrush(Color.FromRgb(0x1D, 0x21, 0x29)));
        t.TextAlignment = TextAlignment.Center;
        t.MaxTextWidth = Math.Max(40, Math.Min(ActualWidth, ActualHeight) * 0.22);
        dc.DrawText(t, new Point(center.X - t.Width / 2, center.Y - (subtitle.Length > 0 ? 12 : 8)));

        if (subtitle.Length > 0)
        {
            var s = MakeText(subtitle, 10, new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
            s.TextAlignment = TextAlignment.Center;
            dc.DrawText(s, new Point(center.X - s.Width / 2, center.Y + 6));
        }
    }

    private FormattedText MakeText(string text, double size, Brush brush) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Microsoft YaHei, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        size,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..Math.Max(1, max - 1)] + "…";

    private static Point PointAt(Point center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var arc = Hit(e.GetPosition(this));
        ToolTip = arc is null ? null : $"{arc.Node.Name}\n{arc.Node.SizeDisplay}";
        Cursor = arc is null ? Cursors.Arrow : Cursors.Hand;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ToolTip = null;
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var arc = Hit(e.GetPosition(this));
        if (arc is null) return;

        if (arc.Node.Kind == NodeKind.Directory && arc.Node.IsScanned && arc.Node.Children.Count > 0 && !arc.Node.IsAggregate)
            DirectoryActivated?.Invoke(arc.Node);
        else
            FileActivated?.Invoke(arc.Node);
    }

    private Arc? Hit(Point point)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var outer = Math.Max(30, Math.Min(ActualWidth, ActualHeight) / 2 - 10);
        var inner = Math.Max(26, outer * 0.30);
        var distance = Math.Sqrt(Math.Pow(point.X - center.X, 2) + Math.Pow(point.Y - center.Y, 2));
        if (distance < inner || distance > outer) return null;

        for (var i = _arcs.Count - 1; i >= 0; i--)
        {
            if (_arcs[i].Geometry.FillContains(point)) return _arcs[i];
        }
        return null;
    }
}

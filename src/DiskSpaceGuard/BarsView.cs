using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskSpaceGuard.Models;
using DiskSpaceGuard.Services;

namespace DiskSpaceGuard.Controls;

/// <summary>条形图：Top 20，其余折叠为一行汇总（§6）。</summary>
public sealed class BarsView : FrameworkElement
{
    private sealed class Row
    {
        public required ScanNode Node { get; init; }
        public Rect Bounds { get; init; }
    }

    private readonly List<Row> _rows = new();
    private ScanNode? _root;

    public BarsView()
    {
        ClipToBounds = true;
    }

    public ScanNode? Root
    {
        get => _root;
        set { _root = value; InvalidateVisual(); }
    }

    public TreemapColorMode ColorMode { get; set; } = TreemapColorMode.Type;
    public int MaxRows { get; set; } = 20;

    public event Action<ScanNode>? DirectoryActivated;
    public event Action<ScanNode>? FileActivated;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _rows.Clear();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_root is null || _root.Children.Count == 0)
        {
            DrawEmpty(dc);
            return;
        }

        // 行高固定，按可用高度决定可显示行数（上限 MaxRows）；其余折叠为一行汇总。
        const double rowHeight = 20.0;
        const double gap = 5.0;
        var capacity = Math.Max(3, (int)((ActualHeight - 26) / (rowHeight + gap)));
        var shown = Math.Min(MaxRows, capacity);

        // Top-K 小顶堆：O(m log K)，避免对上万子项做全量 OrderByDescending。
        var heap = new PriorityQueue<ScanNode, long>();
        var rest = new List<ScanNode>();
        foreach (var child in _root.Children)
        {
            if (child.LogicalSize <= 0) continue;
            if (heap.Count < shown) heap.Enqueue(child, child.LogicalSize);
            else if (heap.TryPeek(out _, out var smallest) && child.LogicalSize > smallest)
            {
                rest.Add(heap.Dequeue());
                heap.Enqueue(child, child.LogicalSize);
            }
            else rest.Add(child);
        }

        var rows = heap.UnorderedItems.Select(p => p.Element)
            .OrderByDescending(c => c.LogicalSize).ToList();

        var total = Math.Max(1, _root.LogicalSize);
        var max = Math.Max(1, rows.Count > 0 ? rows[0].LogicalSize : 1);
        var labelWidth = Math.Clamp(ActualWidth * 0.34, 120, 320);
        var valueWidth = 118.0;
        var barLeft = labelWidth + 10;
        var barWidth = Math.Max(20, ActualWidth - barLeft - valueWidth - 8);

        var y = 6.0;
        foreach (var node in rows)
        {
            if (y + rowHeight > ActualHeight) break;
            var color = VisualColors.For(node, ColorMode, 0, max);
            var width = Math.Max(2, barWidth * node.LogicalSize / max);
            var track = new Rect(barLeft, y + 4, barWidth, rowHeight - 8);
            var fill = new Rect(barLeft, y + 4, width, rowHeight - 8);

            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF5)), null, track, 4, 4);
            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, fill, 4, 4);

            var name = MakeText(node.Name, 11.5, new SolidColorBrush(Color.FromRgb(0x1D, 0x21, 0x29)));
            name.MaxTextWidth = labelWidth - 4;
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(name, new Point(4, y + 4));

            var percent = node.LogicalSize / (double)total * 100;
            var value = MakeText($"{ScanNode.FormatBytes(node.LogicalSize)}  {percent:0.0}%", 10.5,
                new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
            dc.DrawText(value, new Point(barLeft + barWidth + 8, y + 4));

            _rows.Add(new Row { Node = node, Bounds = new Rect(0, y, ActualWidth, rowHeight) });
            y += rowHeight + gap;
        }

        if (rest.Count > 0 && y + rowHeight <= ActualHeight)
        {
            var restSize = rest.Sum(r => r.LogicalSize);
            var text = MakeText(string.Format(T("Chart.RestSummary"), rest.Count,
                ScanNode.FormatBytes(restSize)), 10.5,
                new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
            dc.DrawText(text, new Point(4, y + 4));
        }
    }

    private void DrawEmpty(DrawingContext dc)
    {
        var text = MakeText(T("Chart.Empty"), 12, VisualColors.MutedText);
        dc.DrawText(text, new Point(Math.Max(0, (ActualWidth - text.Width) / 2), Math.Max(0, (ActualHeight - text.Height) / 2)));
    }

    private static string T(string key) => LocalizationService.Text(key, Loc.Instance.Language);

    private FormattedText MakeText(string text, double size, Brush brush) => new(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Microsoft YaHei, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        size,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var row = Hit(e.GetPosition(this));
        ToolTip = row is null ? null : $"{row.Name}\n{row.SizeDisplay}";
        Cursor = row is null ? Cursors.Arrow : Cursors.Hand;
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
        var row = Hit(e.GetPosition(this));
        if (row is null) return;

        if (row.Kind == NodeKind.Directory && row.IsScanned && row.Children.Count > 0 && !row.IsAggregate)
            DirectoryActivated?.Invoke(row);
        else
            FileActivated?.Invoke(row);
    }

    private ScanNode? Hit(Point point)
    {
        foreach (var row in _rows)
        {
            if (row.Bounds.Contains(point)) return row.Node;
        }
        return null;
    }
}

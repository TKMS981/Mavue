using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Mavue.Viewer.Controls;

/// <summary>
/// A checkerboard drawn behind a transparent image, exactly the image's size (it asks for no size of its own and
/// fills what its parent gives it). Each row of cells is one rectangle with a repeating two-color gradient, offset by
/// one cell on every other row, so a large image needs a few hundred elements, not tens of thousands of cells.
/// </summary>
public sealed partial class TransparencyGrid : Microsoft.UI.Xaml.Controls.Panel
{
    /// <summary>Side of one cell in device-independent pixels.</summary>
    public const double CellSize = 8;

    private Color _first = Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C);
    private Color _second = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A);
    private LinearGradientBrush? _even;
    private LinearGradientBrush? _odd;

    public TransparencyGrid()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, e) => EnsureRows(e.NewSize.Height);
    }

    /// <summary>The two cell colors (theme).</summary>
    public void SetColors(Color first, Color second)
    {
        (_first, _second) = (first, second);
        _even = null;
        _odd = null;
        foreach (UIElement child in Children)
        {
            ((Rectangle)child).Fill = null;
        }

        EnsureRows(ActualHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(availableSize.Width, CellSize));
        }

        return new Size(0, 0); // never makes the image's box larger
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < Children.Count; i++)
        {
            double top = i * CellSize;
            Children[i].Arrange(new Rect(0, top, finalSize.Width, Math.Max(0, Math.Min(CellSize, finalSize.Height - top))));
        }

        Clip = new RectangleGeometry { Rect = new Rect(0, 0, finalSize.Width, finalSize.Height) };
        return finalSize;
    }

    private void EnsureRows(double height)
    {
        int rows = height > 0 && double.IsFinite(height) ? (int)Math.Ceiling(height / CellSize) : 0;
        while (Children.Count > rows)
        {
            Children.RemoveAt(Children.Count - 1);
        }

        while (Children.Count < rows)
        {
            Children.Add(new Rectangle());
        }

        _even ??= RowBrush(offset: 0);
        _odd ??= RowBrush(offset: CellSize);
        for (int i = 0; i < Children.Count; i++)
        {
            var row = (Rectangle)Children[i];
            row.Fill ??= i % 2 == 0 ? _even : _odd;
        }
    }

    private LinearGradientBrush RowBrush(double offset)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(offset, 0),
            EndPoint = new Point(offset + (2 * CellSize), 0),
            SpreadMethod = GradientSpreadMethod.Repeat,
        };
        brush.GradientStops.Add(new GradientStop { Color = _first, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = _first, Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = _second, Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = _second, Offset = 1 });
        return brush;
    }
}

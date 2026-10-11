using System.Windows;
using System.Windows.Controls;

namespace ClubOS.Overlay;

/// <summary>
/// Сетка плиток на всю ширину: сколько плиток влезает в ряд по минимальной ширине, столько и ставим, а ширину делим поровну, чтобы справа не оставалось пустой полосы.
/// Высота плитки либо фиксированная (FixedHeight), либо считается от ширины: ширина x HeightPerWidth + ExtraHeight (постер 2 на 3 плюс место под название).
/// </summary>
public sealed class UniformWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = Register(nameof(MinItemWidth), 170.0);
    public static readonly DependencyProperty GapProperty = Register(nameof(Gap), 16.0);
    public static readonly DependencyProperty HeightPerWidthProperty = Register(nameof(HeightPerWidth), 0.0);
    public static readonly DependencyProperty ExtraHeightProperty = Register(nameof(ExtraHeight), 0.0);
    public static readonly DependencyProperty FixedHeightProperty = Register(nameof(FixedHeight), 0.0);

    private static DependencyProperty Register(string name, double value) =>
        DependencyProperty.Register(name, typeof(double), typeof(UniformWrapPanel),
            new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double HeightPerWidth { get => (double)GetValue(HeightPerWidthProperty); set => SetValue(HeightPerWidthProperty, value); }
    public double ExtraHeight { get => (double)GetValue(ExtraHeightProperty); set => SetValue(ExtraHeightProperty, value); }
    public double FixedHeight { get => (double)GetValue(FixedHeightProperty); set => SetValue(FixedHeightProperty, value); }

    /// <summary>Сколько плиток в ряду и какого размера при данной ширине. Вынесено отдельно: проверяется тестом.</summary>
    public (int Columns, Size Item) Layout(double width)
    {
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0) width = MinItemWidth;
        var columns = Math.Max(1, (int)Math.Floor((width + Gap) / (MinItemWidth + Gap)));
        var itemWidth = Math.Floor((width - Gap * (columns - 1)) / columns);
        var itemHeight = FixedHeight > 0 ? FixedHeight : Math.Round(itemWidth * HeightPerWidth + ExtraHeight);
        return (columns, new Size(itemWidth, itemHeight));
    }

    protected override Size MeasureOverride(Size available)
    {
        var (columns, item) = Layout(available.Width);
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        foreach (var child in visible) child.Measure(item);
        var rows = (visible.Count + columns - 1) / columns;
        var width = double.IsInfinity(available.Width) ? columns * item.Width + (columns - 1) * Gap : available.Width;
        return new Size(width, rows == 0 ? 0 : rows * item.Height + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, item) = Layout(finalSize.Width);
        var index = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var row = index / columns;
            var column = index % columns;
            child.Arrange(new Rect(column * (item.Width + Gap), row * (item.Height + Gap), item.Width, item.Height));
            index++;
        }
        return finalSize;
    }
}

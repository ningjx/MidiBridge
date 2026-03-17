using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MidiBridge.Controls;

public class DropIndicatorAdorner : Adorner
{
    private readonly Rectangle _indicator;
    private int _targetIndex = -1;
    private readonly double _itemHeight;
    private readonly double _itemWidth;
    private readonly double _spacing = 6;

    public DropIndicatorAdorner(UIElement adornedElement, double itemHeight, double itemWidth)
        : base(adornedElement)
    {
        _itemHeight = itemHeight;
        _itemWidth = itemWidth;
        IsHitTestVisible = false;

        _indicator = new Rectangle
        {
            Width = itemWidth,
            Height = itemHeight,
            Stroke = new SolidColorBrush(Color.FromRgb(74, 144, 217)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            RadiusX = 6,
            RadiusY = 6
        };

        AddVisualChild(_indicator);
    }

    public void UpdatePosition(int targetIndex)
    {
        _targetIndex = targetIndex;
        InvalidateArrange();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_targetIndex >= 0)
        {
            double y = _targetIndex * (_itemHeight + _spacing) + _spacing / 2;
            _indicator.Arrange(new Rect(3, y, _indicator.Width, _indicator.Height));
        }
        else
        {
            _indicator.Arrange(new Rect(-1000, -1000, 0, 0));
        }
        return finalSize;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        _indicator.Measure(constraint);
        return base.MeasureOverride(constraint);
    }

    protected override Visual GetVisualChild(int index) => _indicator;
    protected override int VisualChildrenCount => 1;
}
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MidiBridge.Controls;

public class DragAdorner : Adorner
{
    private readonly Image _image;
    private double _left;
    private double _top;

    public DragAdorner(UIElement adornedElement, BitmapSource bitmap, double width, double height)
        : base(adornedElement)
    {
        IsHitTestVisible = false;

        _image = new Image
        {
            Source = bitmap,
            Width = width,
            Height = height,
            Stretch = Stretch.None
        };

        AddVisualChild(_image);
    }

    public void SetPosition(double left, double top)
    {
        _left = left;
        _top = top;
        _image.Margin = new Thickness(left, top, 0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _image.Arrange(new Rect(0, 0, _image.Width, _image.Height));
        return finalSize;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        _image.Measure(new Size(_image.Width, _image.Height));
        return new Size(_image.Width, _image.Height);
    }

    protected override Visual GetVisualChild(int index) => _image;
    protected override int VisualChildrenCount => 1;
}
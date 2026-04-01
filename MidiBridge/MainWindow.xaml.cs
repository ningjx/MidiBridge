using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using MidiBridge.Controls;
using MidiBridge.Models;
using MidiBridge.ViewModels;
using MidiBridge.Services.NetworkMidi2;

namespace MidiBridge;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;
    private readonly Dictionary<string, MidiRoute> _subscribedRoutes = new();
    private readonly Dictionary<string, Color> _deviceColors = new();
    private readonly Dictionary<string, Path> _connectionPaths = new();
    private static readonly Random _random = new();
    private int _colorIndex;
    
    private static readonly Color[] _predefinedColors = new[]
    {
        Color.FromRgb(74, 144, 217),
        Color.FromRgb(92, 184, 92),
        Color.FromRgb(240, 173, 78),
        Color.FromRgb(217, 83, 79),
        Color.FromRgb(153, 102, 204),
        Color.FromRgb(64, 196, 255),
        Color.FromRgb(255, 140, 0),
        Color.FromRgb(0, 206, 209),
        Color.FromRgb(255, 105, 180),
        Color.FromRgb(50, 205, 50),
        Color.FromRgb(255, 215, 0),
        Color.FromRgb(138, 43, 226),
        Color.FromRgb(0, 191, 255),
        Color.FromRgb(255, 99, 71),
        Color.FromRgb(46, 139, 87),
    };

    private bool _isDragging;
    private bool _isRealDragging;
    private string? _draggedDeviceId;
    private bool _dragIsInput;
    private Border? _draggedBorder;
    private ContentPresenter? _draggedContainer;
    private Rectangle? _dropIndicator;
    private Canvas? _dropCanvas;

    private int _currentDropIndex = -1;
    private int _dragSourceIndex = -1;
    private double _dragInitialY;
    private TranslateTransform? _dragTransform;

    public MainWindow()
    {
        DataContext = App.MainViewModel;
        
        var config = App.MainViewModel.ConfigService.Config.Window;
        if (!double.IsNaN(config.Left) && !double.IsNaN(config.Top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = config.Left;
            Top = config.Top;
        }
        if (config.Width > 0 && config.Height > 0)
        {
            Width = config.Width;
            Height = config.Height;
        }
        
        InitializeComponent();

        TransmitIndicatorManager.TransmitPulse += OnTransmitPulse;
        
        if (config.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
        
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (VM.Routes is INotifyCollectionChanged routes)
        {
            routes.CollectionChanged += Routes_CollectionChanged;
        }
        SizeChanged += (s, args) => DrawConnections();
        DrawConnections();

        VM.Initialize();
    }

    private void Routes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (MidiRoute route in e.NewItems)
            {
                route.PropertyChanged += Route_PropertyChanged;
                _subscribedRoutes[route.Id] = route;
            }
        }

        if (e.OldItems != null)
        {
            foreach (MidiRoute route in e.OldItems)
            {
                route.PropertyChanged -= Route_PropertyChanged;
                _subscribedRoutes.Remove(route.Id);
            }
        }

        Dispatcher.Invoke(DrawConnections);
    }

    private void Route_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MidiRoute.IsTransmitting) || 
            e.PropertyName == nameof(MidiRoute.IsEnabled) ||
            e.PropertyName == nameof(MidiRoute.IsEffectivelyEnabled))
        {
            Dispatcher.Invoke(DrawConnections);
        }
    }

    private void InputConnector_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            var border = FindParent<Border>(element);
            if (border?.Tag is string deviceId)
            {
                VM.SelectInputDevice(deviceId);
                DrawConnections();
            }
        }
        e.Handled = true;
    }

    private void OutputConnector_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            var border = FindParent<Border>(element);
            if (border?.Tag is string deviceId)
            {
                VM.SelectOutputDevice(deviceId);
                DrawConnections();
            }
        }
        e.Handled = true;
    }

    private void NM2Device_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is NetworkMidi2Protocol.DiscoveredDevice device)
        {
            VM.SelectedDiscoveredDevice = device;
            VM.InviteNM2DeviceCommand.Execute(null);
        }
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T typed)
                return typed;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        var isMaximized = WindowState == WindowState.Maximized;
        var left = isMaximized ? RestoreBounds.Left : Left;
        var top = isMaximized ? RestoreBounds.Top : Top;
        var width = isMaximized ? RestoreBounds.Width : Width;
        var height = isMaximized ? RestoreBounds.Height : Height;

        VM.SaveConfig(left, top, width, height, isMaximized);
        VM.Cleanup();
    }

    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        DrawConnections();
    }

    private void DrawConnections()
    {
        if (ConnectionCanvas == null || ConnectionCanvas.ActualWidth == 0) return;

        ConnectionCanvas.Children.Clear();
        _connectionPaths.Clear();

        foreach (var route in VM.Routes)
        {
            var inputBorder = FindDeviceBorder(route.Source.Id, isInput: true);
            var outputBorder = FindDeviceBorder(route.Target.Id, isInput: false);

            if (inputBorder == null || outputBorder == null) continue;

            var startPoint = inputBorder.TranslatePoint(new Point(inputBorder.ActualWidth, inputBorder.ActualHeight / 2), ConnectionCanvas);
            var endPoint = outputBorder.TranslatePoint(new Point(0, outputBorder.ActualHeight / 2), ConnectionCanvas);

            if (startPoint.X < 0 || startPoint.Y < 0 || endPoint.X < 0 || endPoint.Y < 0) continue;

            var path = CreateBezierPath(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y, GetRouteColor(route.Source.Id), route);
            path.MouseRightButtonDown += Connection_RightClick;
            ConnectionCanvas.Children.Add(path);
            _connectionPaths[route.Id] = path;
        }
    }

    private Border? FindDeviceBorder(string deviceId, bool isInput)
    {
        var scrollViewer = isInput ? InputScrollViewer : OutputScrollViewer;
        if (scrollViewer == null) return null;

        var itemsControl = FindVisualChild<ItemsControl>(scrollViewer);
        if (itemsControl == null) return null;

        var device = isInput
            ? VM.InputDevices.FirstOrDefault(d => d.Id == deviceId)
            : VM.OutputDevices.FirstOrDefault(d => d.Id == deviceId);

        if (device == null) return null;

        var container = itemsControl.ItemContainerGenerator.ContainerFromItem(device) as ContentPresenter;
        if (container == null) return null;

        return FindVisualChild<Border>(container);
    }

    private Ellipse? FindConnectorEllipse(string deviceId, bool isInput)
    {
        var scrollViewer = isInput ? InputScrollViewer : OutputScrollViewer;
        if (scrollViewer == null) return null;

        var itemsControl = FindVisualChild<ItemsControl>(scrollViewer);
        if (itemsControl == null) return null;

        var device = isInput
            ? VM.InputDevices.FirstOrDefault(d => d.Id == deviceId)
            : VM.OutputDevices.FirstOrDefault(d => d.Id == deviceId);

        if (device == null) return null;

        var container = itemsControl.ItemContainerGenerator.ContainerFromItem(device) as ContentPresenter;
        if (container == null) return null;

        return FindVisualChild<Ellipse>(container);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                return typed;

            var result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private void Connection_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Path path && path.Tag is string routeId)
        {
            VM.RemoveRouteCommand.Execute(VM.Routes.FirstOrDefault(r => r.Id == routeId));
            DrawConnections();
        }
    }

    private void Connection_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Path path && path.Tag is string routeId)
        {
            VM.ToggleRouteEnabled(routeId);
            DrawConnections();
        }
        e.Handled = true;
    }

    private Path CreateBezierPath(double x1, double y1, double x2, double y2, Color color, MidiRoute route)
    {
        double distance = x2 - x1;
        double controlOffset = Math.Max(50, distance * 0.4);

        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new Point(x1, y1) };

        figure.Segments.Add(new BezierSegment(
            new Point(x1 + controlOffset, y1),
            new Point(x2 - controlOffset, y2),
            new Point(x2, y2),
            true));

        geometry.Figures.Add(figure);

        Color lineColor;
        if (!route.IsEnabled)
        {
            lineColor = Color.FromRgb(128, 128, 128);
        }
        else if (!route.IsEffectivelyEnabled)
        {
            byte r = (byte)((color.R * 0.25) + (128 * 0.75));
            byte g = (byte)((color.G * 0.25) + (128 * 0.75));
            byte b = (byte)((color.B * 0.25) + (128 * 0.75));
            lineColor = Color.FromRgb(r, g, b);
        }
        else
        {
            lineColor = color;
        }

        var path = new Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(lineColor),
            StrokeThickness = route.IsEffectivelyEnabled ? 3 : 2,
            ToolTip = route.IsEnabled ? "左键禁用 | 右键删除" : "左键启用 | 右键删除",
            Cursor = Cursors.Hand,
            Tag = route.Id,
            IsHitTestVisible = true
        };

        path.MouseLeftButtonDown += Connection_LeftClick;
        path.MouseEnter += Connection_MouseEnter;
        path.MouseLeave += Connection_MouseLeave;

        if (route.IsEffectivelyEnabled && route.IsTransmitting)
        {
            path.Effect = new DropShadowEffect
            {
                Color = color,
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 1
            };
        }

        return path;
    }

    private void Connection_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Path path)
        {
            var thicknessAnim = new DoubleAnimation(path.StrokeThickness + 2, TimeSpan.FromMilliseconds(100));
            path.BeginAnimation(Path.StrokeThicknessProperty, thicknessAnim);
            
            if (path.Effect == null)
            {
                path.Effect = new DropShadowEffect
                {
                    Color = Colors.White,
                    BlurRadius = 0,
                    ShadowDepth = 0,
                    Opacity = 0.5
                };
            }
            
            if (path.Effect is DropShadowEffect effect)
            {
                var blurAnim = new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(100));
                effect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
            }
        }
    }

    private void Connection_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Path path)
        {
            var currentThickness = path.StrokeThickness;
            var targetThickness = currentThickness > 3 ? 3 : 2;
            var thicknessAnim = new DoubleAnimation(targetThickness, TimeSpan.FromMilliseconds(100));
            path.BeginAnimation(Path.StrokeThicknessProperty, thicknessAnim);
            
            if (path.Effect is DropShadowEffect effect && effect.Color == Colors.White)
            {
                var blurAnim = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(100));
                effect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
            }
        }
    }

    private Color GetRouteColor(string sourceId)
    {
        if (_deviceColors.TryGetValue(sourceId, out var color))
        {
            return color;
        }

        color = _predefinedColors[_colorIndex % _predefinedColors.Length];
        _colorIndex++;
        _deviceColors[sourceId] = color;
        return color;
    }

    private void Device_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string deviceId)
        {
            _isDragging = true;
            _isRealDragging = false;
            _draggedDeviceId = deviceId;
            _dragIsInput = VM.InputDevices.Any(d => d.Id == deviceId);
            _draggedBorder = border;

            var scrollViewer = _dragIsInput ? InputScrollViewer : OutputScrollViewer;
            _dragInitialY = e.GetPosition(scrollViewer).Y;

            var devices = _dragIsInput ? VM.InputDevices : VM.OutputDevices;
            var draggedDevice = devices.FirstOrDefault(d => d.Id == _draggedDeviceId);
            if (draggedDevice != null)
            {
                _dragSourceIndex = devices.IndexOf(draggedDevice);
            }

            var itemsControl = _dragIsInput ? InputItemsControl : OutputItemsControl;
            _draggedContainer = itemsControl?.ItemContainerGenerator.ContainerFromItem(draggedDevice) as ContentPresenter;

            _dragTransform = border.RenderTransform as TranslateTransform;
            if (_dragTransform == null)
            {
                _dragTransform = new TranslateTransform();
                border.RenderTransform = _dragTransform;
            }

            border.CaptureMouse();
        }
        e.Handled = true;
    }

    private void Device_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _draggedDeviceId == null || _draggedBorder == null || _dragTransform == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var scrollViewer = _dragIsInput ? InputScrollViewer : OutputScrollViewer;
        if (scrollViewer == null) return;

        var currentY = e.GetPosition(scrollViewer).Y;
        var offsetY = currentY - _dragInitialY;

        if (!_isRealDragging && Math.Abs(offsetY) > 3)
        {
            _isRealDragging = true;
            if (_draggedContainer != null)
            {
                Panel.SetZIndex(_draggedContainer, 1000);
            }
            ShowDropIndicator();
        }

        if (_isRealDragging)
        {
            _dragTransform.Y = offsetY;

            var position = e.GetPosition(scrollViewer);
            var devices = _dragIsInput ? VM.InputDevices : VM.OutputDevices;
            int targetIndex = CalculateTargetIndex(position.Y, devices.Count, _draggedBorder.ActualHeight);

            if (targetIndex != _currentDropIndex)
            {
                _currentDropIndex = targetIndex;
                UpdateDropIndicator();
                UpdateItemOffsets();
            }

            DrawConnections();
        }
    }

    private void ShowDropIndicator()
    {
        _dropCanvas = _dragIsInput ? InputDropCanvas : OutputDropCanvas;
        if (_dropCanvas == null || _draggedBorder == null) return;

        _dropIndicator = new Rectangle
        {
            Width = _draggedBorder.ActualWidth,
            Height = _draggedBorder.ActualHeight,
            Fill = Brushes.Transparent,
            Stroke = new SolidColorBrush(Color.FromRgb(74, 144, 217)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            RadiusX = 6,
            RadiusY = 6
        };

        _dropCanvas.Children.Add(_dropIndicator);
        UpdateDropIndicator();
    }

    private void UpdateDropIndicator()
    {
        if (_dropIndicator == null) return;

        double spacing = 6;
        double y = _currentDropIndex * (_dropIndicator.Height + spacing) + spacing / 2;
        Canvas.SetLeft(_dropIndicator, 3);
        Canvas.SetTop(_dropIndicator, y);
    }

    private void UpdateItemOffsets()
    {
        var scrollViewer = _dragIsInput ? InputScrollViewer : OutputScrollViewer;
        if (scrollViewer == null) return;

        var itemsControl = FindVisualChild<ItemsControl>(scrollViewer);
        if (itemsControl == null) return;

        var devices = _dragIsInput ? VM.InputDevices : VM.OutputDevices;
        double itemHeight = _draggedBorder?.ActualHeight ?? 0;
        double spacing = 6;

        for (int i = 0; i < devices.Count; i++)
        {
            var container = itemsControl.ItemContainerGenerator.ContainerFromItem(devices[i]) as ContentPresenter;
            if (container == null) continue;

            var border = FindVisualChild<Border>(container);
            if (border == null || border == _draggedBorder) continue;

            double offsetY = 0;
            
            if (_dragSourceIndex >= 0 && _currentDropIndex >= 0)
            {
                if (_dragSourceIndex < _currentDropIndex)
                {
                    if (i > _dragSourceIndex && i <= _currentDropIndex)
                    {
                        offsetY = -(itemHeight + spacing);
                    }
                }
                else if (_dragSourceIndex > _currentDropIndex)
                {
                    if (i >= _currentDropIndex && i < _dragSourceIndex)
                    {
                        offsetY = itemHeight + spacing;
                    }
                }
            }

            if (offsetY == 0)
            {
                border.RenderTransform = null;
            }
            else
            {
                border.RenderTransform = new TranslateTransform(0, offsetY);
            }
        }
    }

    private void ResetItemOffsets()
    {
        var scrollViewer = _dragIsInput ? InputScrollViewer : OutputScrollViewer;
        if (scrollViewer == null) return;

        var itemsControl = FindVisualChild<ItemsControl>(scrollViewer);
        if (itemsControl == null) return;

        var devices = _dragIsInput ? VM.InputDevices : VM.OutputDevices;

        foreach (var device in devices)
        {
            var container = itemsControl.ItemContainerGenerator.ContainerFromItem(device) as ContentPresenter;
            if (container == null) continue;

            var border = FindVisualChild<Border>(container);
            if (border == null) continue;

            border.RenderTransform = null;
        }
    }

    private int CalculateTargetIndex(double mouseY, int deviceCount, double itemHeight)
    {
        if (deviceCount == 0) return 0;
        double totalHeight = itemHeight + 6;
        int index = (int)(mouseY / totalHeight);
        return Math.Max(0, Math.Min(index, deviceCount - 1));
    }

private void EndDrag(bool performMove = false)
    {
        if (performMove && _isRealDragging && _draggedDeviceId != null && _draggedBorder != null)
        {
            var scrollViewer = _dragIsInput ? InputScrollViewer : OutputScrollViewer;
            var devices = _dragIsInput ? VM.InputDevices : VM.OutputDevices;

            if (scrollViewer != null && devices.Count > 0 && _currentDropIndex >= 0)
            {
                var draggedDevice = devices.FirstOrDefault(d => d.Id == _draggedDeviceId);
                if (draggedDevice != null)
                {
                    int sourceIndex = devices.IndexOf(draggedDevice);
                    if (sourceIndex >= 0 && sourceIndex != _currentDropIndex)
                    {
                        devices.Move(sourceIndex, _currentDropIndex);
                        VM.SaveDeviceOrder();
                        DrawConnections();
                    }
                }
            }
        }

        ResetItemOffsets();

        if (_draggedContainer != null)
        {
            Panel.SetZIndex(_draggedContainer, 0);
        }

        if (_draggedBorder != null)
        {
            _draggedBorder.RenderTransform = null;
            _draggedBorder.ReleaseMouseCapture();
        }

        if (_dropCanvas != null && _dropIndicator != null)
        {
            _dropCanvas.Children.Remove(_dropIndicator);
            _dropIndicator = null;
        }

        _isDragging = false;
        _isRealDragging = false;
        _draggedDeviceId = null;
        _draggedBorder = null;
        _draggedContainer = null;
        _dragTransform = null;
        _dropCanvas = null;
        _currentDropIndex = -1;
        _dragSourceIndex = -1;

        Dispatcher.BeginInvoke(DrawConnections, System.Windows.Threading.DispatcherPriority.Loaded);
    }

protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            EndDrag(performMove: _isRealDragging);
        }
        base.OnMouseUp(e);
    }

    private void Device_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.ContextMenu != null)
        {
            border.ContextMenu.PlacementTarget = border;
            border.ContextMenu.IsOpen = true;
        }
        e.Handled = true;
    }

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.PlacementTarget is Border border)
        {
            if (border.DataContext is MidiDevice device)
            {
                foreach (var item in menu.Items)
                {
                    if (item is MenuItem menuItem)
                    {
                        menuItem.DataContext = VM;
                        menuItem.Command = VM.ToggleDeviceEnabledCommand;
                        menuItem.CommandParameter = device.Id;
                        menuItem.Header = device.EnabledText;
                    }
                }
            }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ConnectNM2Button_Click(object sender, RoutedEventArgs e)
    {
        if (!VM.IsNetworkRunning) return;

        var dialog = new ConnectDeviceDialog();
        dialog.Owner = this;
        
        if (dialog.ShowDialog() == true)
        {
            VM.ConnectNM2Device(dialog.DeviceIp, dialog.DevicePort);
        }
    }

    private void Device_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            var effect = new DropShadowEffect
            {
                Color = Color.FromRgb(74, 144, 217),
                BlurRadius = 0,
                ShadowDepth = 0,
                Opacity = 0
            };
            border.Effect = effect;
            
            var blurAnim = new DoubleAnimation(15, TimeSpan.FromMilliseconds(150));
            var opacityAnim = new DoubleAnimation(0.7, TimeSpan.FromMilliseconds(150));
            effect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
            effect.BeginAnimation(DropShadowEffect.OpacityProperty, opacityAnim);
        }
    }

    private void Device_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border && border.Effect is DropShadowEffect effect)
        {
            var blurAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            var opacityAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            
            blurAnim.Completed += (s, _) => border.Effect = null;
            effect.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
            effect.BeginAnimation(DropShadowEffect.OpacityProperty, opacityAnim);
        }
    }

    private void Connector_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Ellipse ellipse)
        {
            var parent = VisualTreeHelper.GetParent(ellipse) as Grid;
            if (parent != null)
            {
                var glow = parent.Children.OfType<Ellipse>().FirstOrDefault(el => el.Name == "ConnectorGlow");
                if (glow != null)
                {
                    var stroke = ellipse.Stroke as SolidColorBrush;
                    var glowColor = stroke?.Color ?? Color.FromRgb(74, 144, 217);
                    glow.Fill = new SolidColorBrush(glowColor);
                    
                    var opacityAnim = new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(100));
                    glow.BeginAnimation(Ellipse.OpacityProperty, opacityAnim);
                }
            }
        }
    }

    private void Connector_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Ellipse ellipse)
        {
            var parent = VisualTreeHelper.GetParent(ellipse) as Grid;
            if (parent != null)
            {
                var glow = parent.Children.OfType<Ellipse>().FirstOrDefault(el => el.Name == "ConnectorGlow");
                if (glow != null)
                {
                    var opacityAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
                    glow.BeginAnimation(Ellipse.OpacityProperty, opacityAnim);
                }
            }
        }
    }

    private void NM2Device_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            var parent = VisualTreeHelper.GetParent(border) as Grid;
            if (parent != null)
            {
                var glow = parent.Children.OfType<Border>().FirstOrDefault(b => b.Name == "DeviceGlow");
                if (glow != null)
                {
                    var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                    glow.BeginAnimation(Border.OpacityProperty, opacityAnim);
                    glow.BorderBrush = new SolidColorBrush(Color.FromRgb(74, 144, 217));
                }
            }
        }
    }

    private void NM2Device_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            var parent = VisualTreeHelper.GetParent(border) as Grid;
            if (parent != null)
            {
                var glow = parent.Children.OfType<Border>().FirstOrDefault(b => b.Name == "DeviceGlow");
                if (glow != null)
                {
                    var opacityAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
                    glow.BeginAnimation(Border.OpacityProperty, opacityAnim);
                }
            }
        }
    }

    private void EmptyArea_Click(object sender, MouseButtonEventArgs e)
    {
        VM.ClearSelection();
        DrawConnections();
        e.Handled = true;
    }

    private void OnTransmitPulse(object? sender, TransmitEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (e.Target is MidiDevice device)
            {
                AnimateDeviceTransmit(device.Id);
            }
            else if (e.Target is MidiRoute route)
            {
                AnimateRouteTransmit(route.Id);
            }
        });
    }

    private void AnimateDeviceTransmit(string deviceId)
    {
        var inputGlow = FindConnectorGlow(InputItemsControl, deviceId);
        var outputGlow = FindConnectorGlow(OutputItemsControl, deviceId);

        if (inputGlow != null) AnimateGlow(inputGlow, Colors.DodgerBlue);
        if (outputGlow != null) AnimateGlow(outputGlow, Colors.DodgerBlue);
    }

    private Ellipse? FindConnectorGlow(ItemsControl itemsControl, string deviceId)
    {
        foreach (var item in itemsControl.Items)
        {
            var container = itemsControl.ItemContainerGenerator.ContainerFromItem(item) as ContentPresenter;
            if (container == null) continue;

            var device = item as MidiDevice;
            if (device?.Id != deviceId) continue;

            var grid = FindVisualChild<Grid>(container);
            if (grid == null) continue;

            foreach (var child in grid.Children)
            {
                if (child is Grid innerGrid)
                {
                    var glow = innerGrid.Children.OfType<Ellipse>().FirstOrDefault(e => e.Name == "ConnectorGlow");
                    if (glow != null) return glow;
                }
            }
        }
        return null;
    }

    private void AnimateRouteTransmit(string routeId)
    {
        if (!_connectionPaths.TryGetValue(routeId, out var path)) return;

        var storyboard = new Storyboard();

        var colorAnim = new ColorAnimationUsingKeyFrames();
        colorAnim.KeyFrames.Add(new DiscreteColorKeyFrame(Colors.LightGreen, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        colorAnim.KeyFrames.Add(new DiscreteColorKeyFrame(Colors.LightGreen, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))));
        colorAnim.KeyFrames.Add(new EasingColorKeyFrame(((SolidColorBrush)path.Stroke).Color, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));

        Storyboard.SetTargetProperty(colorAnim, new PropertyPath("(Shape.Stroke).(SolidColorBrush.Color)"));
        storyboard.Children.Add(colorAnim);

        var blurAnim = new DoubleAnimationUsingKeyFrames();
        blurAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(15, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blurAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))));
        blurAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));

        if (path.Effect == null)
        {
            path.Effect = new DropShadowEffect { Color = Colors.LightGreen, BlurRadius = 0, ShadowDepth = 0, Opacity = 1 };
        }

        Storyboard.SetTargetProperty(blurAnim, new PropertyPath("(UIElement.Effect).(DropShadowEffect.BlurRadius)"));
        storyboard.Children.Add(blurAnim);

        storyboard.Begin(path);
    }

    private static void AnimateGlow(Ellipse glow, Color color)
    {
        glow.Fill = new RadialGradientBrush
        {
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(200, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
            }
        };

        var storyboard = new Storyboard();

        var opacityAnim = new DoubleAnimationUsingKeyFrames();
        opacityAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacityAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))));
        opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });

        Storyboard.SetTargetProperty(opacityAnim, new PropertyPath("Opacity"));
        storyboard.Children.Add(opacityAnim);

        var blurAnim = new DoubleAnimationUsingKeyFrames();
        blurAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(12, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blurAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))));
        blurAnim.KeyFrames.Add(new EasingDoubleKeyFrame(6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));

        Storyboard.SetTargetProperty(blurAnim, new PropertyPath("(UIElement.Effect).(BlurEffect.Radius)"));
        storyboard.Children.Add(blurAnim);

        storyboard.Begin(glow);
    }
}
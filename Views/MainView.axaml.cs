using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ReGame.ViewModels;

namespace ReGame.Views;

public partial class MainView : UserControl
{
  private const double HoldMilliseconds = 350;    // touch: how long to hold before painting starts
  private const double MoveTolerance = 8;         // touch: moving more than this cancels the hold

  private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(HoldMilliseconds) };
  private Point _pressPoint;
  private IPointer? _pressPointer;
  private CellData? _selectedBeforePress;
  private bool _painting;
  private CellData? _lastPainted;

  public MainView()
  {
    InitializeComponent();
    _holdTimer.Tick += OnHoldTimer;

    // tunnel: we see the press before the ListBox, so a right-click does not change the selection
    Board.AddHandler(InputElement.PointerPressedEvent, OnBoardPointerPressed, RoutingStrategies.Tunnel);
  }

  // ---- double-click enters a cell ----
  private void OnBoardDoubleTapped(object? sender, TappedEventArgs e)
  {
    if (DataContext is MainViewModel vm && (e.Source as StyledElement)?.DataContext is CellData cell)
      vm.Enter(cell);
  }

  // ---- painting: right button (mouse) or long press (touch / pen) ----
  private void OnBoardPointerPressed(object? sender, PointerPressedEventArgs e)
  {
    if (DataContext is not MainViewModel vm) return;

    var point = e.GetCurrentPoint(Board);
    _selectedBeforePress = vm.Selected;          // the brush, before the press changes the selection

    if (e.Pointer.Type == PointerType.Mouse)
    {
      if (point.Properties.IsRightButtonPressed)
      {
        e.Handled = true;
        BeginPaint(vm, e.Pointer, point.Position);
      }
      return;
    }

    _pressPoint = point.Position;
    _pressPointer = e.Pointer;
    _holdTimer.Start();
  }

  private void OnHoldTimer(object? sender, EventArgs e)
  {
    _holdTimer.Stop();
    if (DataContext is not MainViewModel vm || _pressPointer is null) return;

    vm.Selected = _selectedBeforePress;          // the touch selected the cell under the finger: go back to the brush
    BeginPaint(vm, _pressPointer, _pressPoint);
  }

  private void BeginPaint(MainViewModel vm, IPointer pointer, Point position)
  {
    if (!vm.BeginPaint()) return;

    _painting = true;
    pointer.Capture(Board);
    PaintAt(vm, position);
  }

  private void OnBoardPointerMoved(object? sender, PointerEventArgs e)
  {
    var position = e.GetPosition(Board);

    if (_holdTimer.IsEnabled)
    {
      // moved before the hold time passed: it was a normal touch, not a hold
      if (Math.Abs(position.X - _pressPoint.X) > MoveTolerance ||
          Math.Abs(position.Y - _pressPoint.Y) > MoveTolerance)
        _holdTimer.Stop();
      return;
    }

    if (_painting && DataContext is MainViewModel vm)
      PaintAt(vm, position);
  }

  private void PaintAt(MainViewModel vm, Point position)
  {
    var hit = Board.InputHitTest(position) as StyledElement;
    if (hit?.DataContext is CellData cell && cell != _lastPainted)
    {
      _lastPainted = cell;
      vm.Paint(cell);
    }
  }

  private void OnBoardPointerReleased(object? sender, PointerReleasedEventArgs e) => EndPaint(e.Pointer);
  private void OnBoardCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndPaint(null);

  private void EndPaint(IPointer? pointer)
  {
    _holdTimer.Stop();
    if (!_painting) return;

    _painting = false;
    _lastPainted = null;
    pointer?.Capture(null);
    (DataContext as MainViewModel)?.EndPaint();
  }

  // ---- breadcrumb: the "…" button lists the hidden levels ----
  private void OnBreadcrumbOverflowClick(object? sender, RoutedEventArgs e)
  {
    if (sender is not Control target || DataContext is not MainViewModel vm) return;

    var menu = new MenuFlyout();
    foreach (var crumb in vm.OverflowCrumbs)
    {
      menu.Items.Add(new MenuItem
      {
        Header = new TextBlock { Text = crumb.Name, Margin = new Thickness(crumb.Depth * 12, 0, 0, 0) },
        Command = crumb.GoCommand,
      });
    }
    menu.ShowAt(target);
  }
}
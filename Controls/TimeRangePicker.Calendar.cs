using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SetSett.Model;
using SetSett.Text;
using Windows.Foundation;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace SetSett.Controls;

// Calendar mode: a vertical timeline covering this day and the next (48h), so a block can run past midnight.
// One pixel is one minute.
public sealed partial class TimeRangePicker
{
    private const double CalendarGutterWidth = 60;
    private const double CalendarPad = 14;
    private const double CalendarMinutes = 2 * 24 * 60;

    /// <summary>The <see cref="CalendarHeight"/> at which the whole 48-hour axis fits, leaving nothing to scroll inside the picker.</summary>
    public const double CalendarFullHeight = CalendarMinutes + 2 * CalendarPad + 2;

    /// <summary>The y of a time on the calendar axis, so a parent can draw its own shared hour labels in line with the pickers.</summary>
    public static double CalendarAxisY(double minutes) => CalendarPad + minutes;
    private const double HandleReach = 24;
    private const double TouchHold = 350;
    private const double TouchSlop = 10;
    private const double BlockOpacity = 0.92;

    private readonly record struct PendingPress(DragKind Kind, Pointer Pointer, Point Position);

    private bool _calendarBuilt;
    private readonly List<Shapes.Rectangle> _hourLines = [];
    private Shapes.Rectangle? _nextDayShade;
    private Shapes.Rectangle? _pointLine;
    private CursorBorder? _block;
    private TextBlock? _blockText;
    private Border? _bubble;
    private TextBlock? _bubbleText;
    private TimeHandle? _calendarStart;
    private TimeHandle? _calendarEnd;
    private Point _calendarStartCenter;
    private Point _calendarEndCenter;
    private DispatcherQueueTimer? _holdTimer;
    private PendingPress? _pending;
    private Point _panOrigin;
    private double _panOffset;
    private bool _panning;
    private DispatcherQueueTimer? _autoScrollTimer;
    private double _autoScrollVelocity;
    private Point _autoScrollRoot;
    private ScrollViewer? _autoScrollTarget;
    private ScrollViewer? _panTarget;
    private double _autoScrollOffset;

    private double CalendarGutter => ShowTimeAxis ? CalendarGutterWidth : 0;

    private static double MinutesToY(double minutes) => CalendarPad + minutes;

    private static TimeSpan CalendarTimeAt(double y) =>
        TimeSpan.FromMinutes(Math.Clamp(y - CalendarPad, 0, CalendarMinutes));

    private void EnsureCalendar()
    {
        if (_calendarBuilt || _calendarCanvas is not { } canvas)
        {
            return;
        }

        _calendarBuilt = true;
        var culture = CultureInfo.CurrentCulture;
        var use24Hour = Use24Hour;

        canvas.Height = CalendarMinutes + 2 * CalendarPad;
        canvas.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var shade = new Shapes.Rectangle { IsHitTestVisible = false };
        Themed("SubtleFillColorSecondaryBrush", b => shade.Fill = b);
        _nextDayShade = shade;
        canvas.Children.Add(shade);

        _hourLines.Clear();
        for (var hour = 0; hour <= 48; hour++)
        {
            var line = new Shapes.Rectangle { Height = 1, IsHitTestVisible = false };
            Themed("DividerStrokeColorDefaultBrush", b => line.Fill = b);
            Canvas.SetLeft(line, CalendarGutter);
            Canvas.SetTop(line, MinutesToY(hour * 60));
            _hourLines.Add(line);
            canvas.Children.Add(line);

            if (!ShowTimeAxis)
            {
                continue;
            }

            var label = new TextBlock
            {
                Text = TimeRangeFormat.FormatHour(hour, culture, use24Hour),
                Width = CalendarGutter - 10,
                TextAlignment = TextAlignment.Right,
                FontSize = 12,
                IsHitTestVisible = false,
            };
            Themed("TextFillColorSecondaryBrush", b => label.Foreground = b);
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, MinutesToY(hour * 60) - 9);
            canvas.Children.Add(label);
        }

        if (ShowTimeAxis)
        {
            // Under the midnight label in the gutter, where the block cannot cover it.
            var nextDay = new TextBlock
            {
                Text = "Next day",
                Width = CalendarGutter - 10,
                TextAlignment = TextAlignment.Right,
                FontSize = 11,
                IsHitTestVisible = false,
            };
            Themed("TextFillColorTertiaryBrush", b => nextDay.Foreground = b);
            Canvas.SetLeft(nextDay, 0);
            Canvas.SetTop(nextDay, MinutesToY(24 * 60) + 10);
            canvas.Children.Add(nextDay);
        }

        // Without the axis the column is narrow, so the label wraps and sits below the start handle.
        var blockText = new TextBlock
        {
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = ShowTimeAxis ? new Thickness(36, 6, 36, 2) : new Thickness(6, 18, 6, 2),
            IsHitTestVisible = false,
        };
        Themed("TextOnAccentFillColorPrimaryBrush", b => blockText.Foreground = b);
        var block = new CursorBorder { CornerRadius = new CornerRadius(6), Opacity = BlockOpacity };
        block.Children.Add(blockText);
        block.SetCursor(InputSystemCursorShape.SizeAll);

        // Claim touch from the start (see OnCalendarPointerPressed); the claim cannot be added after the finger is down.
        block.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        Themed("AccentFillColorDefaultBrush", b => block.Background = b);
        _block = block;
        _blockText = blockText;
        canvas.Children.Add(block);

        var pointLine = new Shapes.Rectangle { Height = 2, IsHitTestVisible = false };
        Themed("AccentFillColorDefaultBrush", b => pointLine.Fill = b);
        _pointLine = pointLine;
        canvas.Children.Add(pointLine);

        var bubbleText = new TextBlock { FontSize = 12 };
        Themed("TextOnAccentFillColorPrimaryBrush", b => bubbleText.Foreground = b);
        var bubble = new Border
        {
            Child = bubbleText,
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(4),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        Themed("AccentFillColorDefaultBrush", b => bubble.Background = b);
        _bubble = bubble;
        _bubbleText = bubbleText;
        canvas.Children.Add(bubble);

        _calendarStart = CreateHandle(TimeHandleRole.Start, InputSystemCursorShape.SizeNorthSouth);
        _calendarEnd = CreateHandle(TimeHandleRole.End, InputSystemCursorShape.SizeNorthSouth);
        canvas.Children.Add(_calendarStart);
        canvas.Children.Add(_calendarEnd);

        _holdTimer = DispatcherQueue.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(TouchHold);
        _holdTimer.IsRepeating = false;
        _holdTimer.Tick += OnHoldTimerTick;

        LayoutCalendar();
    }

    private void OnCalendarSizeChanged(object sender, SizeChangedEventArgs e) => LayoutCalendar();

    private void LayoutCalendar()
    {
        if (!_calendarBuilt || _calendarScroller is not { } scroller || _calendarCanvas is not { } canvas)
        {
            return;
        }

        var width = scroller.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        canvas.Width = width;
        var lineWidth = Math.Max(0, width - CalendarGutter);
        foreach (var line in _hourLines)
        {
            line.Width = lineWidth;
        }

        if (_nextDayShade is { } shade)
        {
            shade.Width = lineWidth;
            shade.Height = 24 * 60 + CalendarPad;
            Canvas.SetLeft(shade, CalendarGutter);
            Canvas.SetTop(shade, MinutesToY(24 * 60));
        }

        UpdateCalendar(Value);
    }

    private void UpdateCalendar(TimeRangeValue value)
    {
        if (!_calendarBuilt || _calendarCanvas is not { Width: > 0 } canvas ||
            _block is null || _blockText is null || _pointLine is null || _calendarStart is null || _calendarEnd is null)
        {
            return;
        }

        var left = CalendarGutter + 6;
        var right = Math.Max(left + 80, canvas.Width - (ShowTimeAxis ? 18 : 6));
        var startY = MinutesToY(value.Start.TotalMinutes);

        _calendarStartCenter = new Point(left + 22, startY);
        PlaceHandle(_calendarStart, _calendarStartCenter);

        if (value.End is { } end)
        {
            var endY = MinutesToY(end.TotalMinutes);
            var height = Math.Max(2, endY - startY);

            Canvas.SetLeft(_block, left);
            Canvas.SetTop(_block, startY);
            _block.Width = right - left;
            _block.Height = height;
            _block.Visibility = Visibility.Visible;
            _pointLine.Visibility = Visibility.Collapsed;

            // A narrow column (no time axis) needs more room above the label for the start handle, and wraps it.
            var labelTop = ShowTimeAxis ? 22 : 40;
            _blockText.Text = TimeRangeFormat.FormatRange(value, CultureInfo.CurrentCulture, Use24Hour) +
                (height >= 40 ? $"\n{TimeRangeFormat.FormatDuration(value.Duration)}" : string.Empty);
            _blockText.Visibility = height >= labelTop ? Visibility.Visible : Visibility.Collapsed;
            _blockText.MaxHeight = Math.Max(0, height - (ShowTimeAxis ? 8 : 20));

            _calendarEndCenter = new Point(right - 22, endY);
            PlaceHandle(_calendarEnd, _calendarEndCenter);
            _calendarEnd.Visibility = Visibility.Visible;
        }
        else
        {
            _block.Visibility = Visibility.Collapsed;
            _calendarEnd.Visibility = Visibility.Collapsed;

            _pointLine.Width = right - left;
            Canvas.SetLeft(_pointLine, left);
            Canvas.SetTop(_pointLine, startY - 1);
            _pointLine.Visibility = Visibility.Visible;
        }

        UpdateHandleText(_calendarStart, _calendarEnd, value);
        UpdateBubble(value);
    }

    private static void PlaceHandle(TimeHandle handle, Point center)
    {
        Canvas.SetLeft(handle, center.X - handle.Width / 2);
        Canvas.SetTop(handle, center.Y - handle.Height / 2);
    }

    /// <summary>While dragging, shows the time of the edge being moved beside the axis, clear of the finger.</summary>
    private void UpdateBubble(TimeRangeValue value)
    {
        if (_bubble is null || _bubbleText is null)
        {
            return;
        }

        if (_drag == DragKind.None || Mode != TimeRangePickerMode.Calendar)
        {
            _bubble.Visibility = Visibility.Collapsed;
            return;
        }

        var time = _drag == DragKind.End && value.End is { } end ? end : value.Start;
        _bubbleText.Text = TimeRangeFormat.FormatTime(time, CultureInfo.CurrentCulture, Use24Hour);
        _bubble.Visibility = Visibility.Visible;
        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_bubble, ShowTimeAxis ? 2 : CalendarGutter + 34);
        Canvas.SetTop(_bubble, MinutesToY(time.TotalMinutes) - _bubble.DesiredSize.Height / 2);
    }

    private void ScrollCalendarToValue()
    {
        if (!_calendarBuilt || _calendarScroller is not { } scroller)
        {
            return;
        }

        // A viewport tall enough for the whole day starts at midnight; otherwise leave an hour of context above the block.
        var wholeDay = scroller.ActualHeight >= 24 * 60 + 2 * CalendarPad;
        var target = wholeDay ? 0 : Math.Max(0, MinutesToY(Value.Start.TotalMinutes - 60));
        scroller.ChangeView(null, target, null, disableAnimation: true);
    }

    // --- Pointer input ---

    private void OnCalendarPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_calendarBuilt || _calendarCanvas is not { } canvas || _drag != DragKind.None)
        {
            return;
        }

        var point = e.GetCurrentPoint(canvas);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = point.Position;
        var time = CalendarTimeAt(position.Y);
        var touch = e.Pointer.PointerDeviceType == PointerDeviceType.Touch;

        switch (HitTestCalendar(Value, position, touch))
        {
            case DragKind.Start:
                FocusHandle(DragKind.Start);
                BeginPointerDrag(DragKind.Start, e.Pointer, canvas, time, wrap: false);
                break;

            case DragKind.End:
                FocusHandle(DragKind.End);
                BeginPointerDrag(DragKind.End, e.Pointer, canvas, time, wrap: false);
                break;

            case DragKind.Range when touch:
                // The block owns this touch from the start (so the timeline cannot begin its own pan). Holding still picks
                // the block up; moving first scrolls the timeline by hand.
                _pending = new PendingPress(DragKind.Range, e.Pointer, position);
                _panTarget = ScrollTarget();
                if (_panTarget is { } scroller)
                {
                    _panOrigin = e.GetCurrentPoint(scroller).Position;
                    _panOffset = scroller.VerticalOffset;
                }

                _panning = false;
                _holdTimer?.Start();
                break;

            case DragKind.Range:
                FocusHandle(DragKind.Start);
                BeginPointerDrag(DragKind.Range, e.Pointer, canvas, time, wrap: false);
                break;

            default:
                if (touch)
                {
                    // A tap places; a swipe scrolls (and cancels the pointer).
                    _pending = new PendingPress(DragKind.None, e.Pointer, position);
                }
                else
                {
                    // Click places the start, and dragging from there stretches the end, as in calendar apps.
                    var placed = PlaceAt(time);
                    FocusHandle(DragKind.Start);
                    BeginPointerDrag(placed.IsRange ? DragKind.End : DragKind.Start, e.Pointer, canvas, time, wrap: false);
                }

                break;
        }
    }

    private DragKind HitTestCalendar(TimeRangeValue value, Point position, bool touch)
    {
        var startDistance = Distance(position, _calendarStartCenter);
        var endDistance = value.IsRange ? Distance(position, _calendarEndCenter) : double.MaxValue;
        if (Math.Min(startDistance, endDistance) <= HandleReach)
        {
            return startDistance <= endDistance ? DragKind.Start : DragKind.End;
        }

        if (position.X < CalendarGutter)
        {
            return DragKind.None;
        }

        var startY = MinutesToY(value.Start.TotalMinutes);
        if (value.End is not { } end)
        {
            // The bare line is a mouse target only: it does not claim touch, so a finger there scrolls (or taps to place).
            return !touch && Math.Abs(position.Y - startY) <= 12 ? DragKind.Start : DragKind.None;
        }

        return position.Y >= startY && position.Y <= MinutesToY(end.TotalMinutes) ? DragKind.Range : DragKind.None;
    }

    /// <summary>Moves the start to the clicked time, keeping the length.</summary>
    private TimeRangeValue PlaceAt(TimeSpan time)
    {
        var value = Value;
        var placed = TimeRangeMath.MoveRange(value, time - value.Start, Constraints, wrap: false);
        ApplyValue(placed, isInteractive: true);
        return placed;
    }

    private void OnCalendarPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_calendarCanvas is not { } canvas)
        {
            return;
        }

        var position = e.GetCurrentPoint(canvas).Position;
        if (_pending is { } pending && pending.Pointer.PointerId == e.Pointer.PointerId)
        {
            if (pending.Kind == DragKind.Range && _panTarget is { } scroller)
            {
                // Measured against the scroller, not the canvas, which moves under the finger as it scrolls.
                var inScroller = e.GetCurrentPoint(scroller).Position;
                if (_panning || Distance(inScroller, _panOrigin) > TouchSlop)
                {
                    _panning = true;
                    _holdTimer?.Stop();
                    scroller.ChangeView(
                        null,
                        Math.Clamp(_panOffset - (inScroller.Y - _panOrigin.Y), 0, scroller.ScrollableHeight),
                        null,
                        disableAnimation: true);
                }

                return;
            }

            if (Distance(position, pending.Position) > TouchSlop)
            {
                _holdTimer?.Stop();
                _pending = null;
            }

            return;
        }

        if (_drag == DragKind.None || e.Pointer.PointerId != _dragPointerId)
        {
            return;
        }

        ContinueDrag(CalendarTimeAt(position.Y));
        UpdateAutoScroll(e);
    }

    private void OnCalendarPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _holdTimer?.Stop();

        if (_pending is { } pending && pending.Pointer.PointerId == e.Pointer.PointerId)
        {
            _pending = null;
            _panning = false;
            if (pending.Kind == DragKind.None && _calendarCanvas is { } canvas &&
                Distance(e.GetCurrentPoint(canvas).Position, pending.Position) <= TouchSlop)
            {
                PlaceAt(CalendarTimeAt(pending.Position.Y));
                Commit();
            }

            return;
        }

        if (_drag != DragKind.None && e.Pointer.PointerId == _dragPointerId)
        {
            EndPointerDrag();
        }
    }

    private void OnCalendarPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _holdTimer?.Stop();
        _pending = null;
        _panning = false;
        if (_drag != DragKind.None)
        {
            EndPointerDrag();
        }
    }

    private void OnHoldTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_panning || _pending is not { Kind: DragKind.Range } pending || _calendarCanvas is not { } canvas || _drag != DragKind.None)
        {
            return;
        }

        _pending = null;
        FocusHandle(DragKind.Start);
        SetBlockLifted(true);
        BeginPointerDrag(DragKind.Range, pending.Pointer, canvas, CalendarTimeAt(pending.Position.Y), wrap: false);
        UpdateBubble(Value);
    }

    /// <summary>Shows that the block has been picked up (touch has no hover to say so).</summary>
    private void SetBlockLifted(bool lifted)
    {
        if (_block is null)
        {
            return;
        }

        _block.Opacity = lifted ? 1 : BlockOpacity;
        _block.BorderThickness = new Thickness(lifted ? 2 : 0);
        _block.BorderBrush = lifted ? ThemeBrush("TextFillColorPrimaryBrush") : null;
    }

    /// <summary>
    /// While a drag is held near the top or bottom of the viewport, scrolls at a speed that grows with depth into the edge
    /// zone. It runs on a timer, so it keeps going when the pointer is still and does not depend on how fast the mouse moves.
    /// </summary>
    private void UpdateAutoScroll(PointerRoutedEventArgs e)
    {
        if (ScrollTarget() is not { } scroller)
        {
            return;
        }

        // Measured in window coordinates, which stay put while the content scrolls underneath the pointer.
        var root = e.GetCurrentPoint(null).Position;
        var pointerYInScroller = root.Y - scroller.TransformToVisual(null).TransformPoint(new Point(0, 0)).Y;

        const double edge = 36;
        const double maxSpeed = 14;
        var top = edge - pointerYInScroller;
        var bottom = pointerYInScroller - (scroller.ActualHeight - edge);
        _autoScrollVelocity = top > 0 ? -maxSpeed * Math.Min(1, top / edge)
            : bottom > 0 ? maxSpeed * Math.Min(1, bottom / edge)
            : 0;
        _autoScrollRoot = root;
        _autoScrollTarget = scroller;

        if (_autoScrollVelocity == 0)
        {
            _autoScrollTimer?.Stop();
            return;
        }

        if (_autoScrollTimer is null)
        {
            _autoScrollTimer = DispatcherQueue.CreateTimer();
            _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
            _autoScrollTimer.Tick += OnAutoScrollTick;
        }

        if (!_autoScrollTimer.IsRunning)
        {
            _autoScrollOffset = scroller.VerticalOffset;
            _autoScrollTimer.Start();
        }
    }

    private void StopAutoScroll()
    {
        _autoScrollTimer?.Stop();
        _autoScrollVelocity = 0;
    }

    private void OnAutoScrollTick(DispatcherQueueTimer sender, object args)
    {
        if (_drag == DragKind.None || _autoScrollTarget is not { } scroller || _calendarCanvas is not { } canvas)
        {
            sender.Stop();
            return;
        }

        // Track the offset ourselves: ChangeView applies on the next layout, so VerticalOffset lags behind.
        _autoScrollOffset = Math.Clamp(_autoScrollOffset + _autoScrollVelocity, 0, scroller.ScrollableHeight);
        scroller.ChangeView(null, _autoScrollOffset, null, disableAnimation: true);

        // Where the held pointer now sits on the axis, wherever the scrolled content has got to.
        var onCanvas = canvas.TransformToVisual(null).Inverse.TransformPoint(_autoScrollRoot);
        ContinueDrag(CalendarTimeAt(onCanvas.Y));
    }

    /// <summary>
    /// The scroller that should move when this timeline needs to: its own when it is scrollable, otherwise the nearest
    /// scrollable parent (a week view holds tall columns that scroll together).
    /// </summary>
    private ScrollViewer? ScrollTarget()
    {
        if (_calendarScroller is { ScrollableHeight: > 1 } own)
        {
            return own;
        }

        for (var node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer { ScrollableHeight: > 1 } parent)
            {
                return parent;
            }
        }

        return _calendarScroller;
    }
}

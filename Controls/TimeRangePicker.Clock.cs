using System.Globalization;
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

// Clock mode: one revolution is 24 hours with midnight at the top, so an overnight range is just an arc across the top
// and there is no am/pm ambiguity. Start and end are handles on the ring; the arc between them is the selection.
public sealed partial class TimeRangePicker
{
    private const double ClockMargin = 12;
    private const double ClockThickness = 28;

    private bool _clockBuilt;
    private double _clockX;
    private double _clockY;
    private double _clockRadius;
    private Shapes.Path? _clockTrack;
    private Shapes.Path? _clockArc;
    private Shapes.Path? _clockTicks;
    private EllipseGeometry? _trackGeometry;
    private EllipseGeometry? _fullCircle;
    private PathGeometry? _arcGeometry;
    private PathFigure? _arcFigure;
    private ArcSegment? _arcSegment;
    private readonly List<(int Hour, TextBlock Text)> _clockLabels = [];
    private TimeHandle? _clockStart;
    private TimeHandle? _clockEnd;

    private static double TimeToDegrees(TimeSpan time) =>
        TimeRangeMath.WrapToDay(time).Ticks / (double)TimeRangeMath.Day.Ticks * 360;

    private Point PointOnDial(double degrees, double radius)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(_clockX + radius * Math.Sin(radians), _clockY - radius * Math.Cos(radians));
    }

    /// <summary>The clock time a point on the dial points at, by angle only.</summary>
    private TimeSpan ClockTimeAt(Point position)
    {
        var angle = Math.Atan2(position.X - _clockX, -(position.Y - _clockY));
        if (angle < 0)
        {
            angle += 2 * Math.PI;
        }

        return TimeSpan.FromTicks((long)(angle / (2 * Math.PI) * TimeRangeMath.Day.Ticks));
    }

    private void EnsureClock()
    {
        if (_clockBuilt || _clockCanvas is not { } canvas)
        {
            return;
        }

        _clockBuilt = true;
        var culture = CultureInfo.CurrentCulture;
        var use24Hour = Use24Hour;

        _trackGeometry = new EllipseGeometry();
        var track = new Shapes.Path { Data = _trackGeometry, StrokeThickness = ClockThickness, Opacity = 0.3, IsHitTestVisible = false };
        Themed("ControlStrongStrokeColorDefaultBrush", b => track.Stroke = b);
        _clockTrack = track;
        canvas.Children.Add(track);

        var ticks = new Shapes.Path { StrokeThickness = 1.5, IsHitTestVisible = false };
        Themed("TextFillColorTertiaryBrush", b => ticks.Stroke = b);
        _clockTicks = ticks;
        canvas.Children.Add(ticks);

        _clockLabels.Clear();
        foreach (var hour in new[] { 0, 6, 12, 18 })
        {
            var label = new TextBlock
            {
                Text = TimeRangeFormat.FormatHour(hour, culture, use24Hour),
                FontSize = 12,
                IsHitTestVisible = false,
            };
            Themed("TextFillColorSecondaryBrush", b => label.Foreground = b);
            _clockLabels.Add((hour, label));
            canvas.Children.Add(label);
        }

        _fullCircle = new EllipseGeometry();
        _arcSegment = new ArcSegment { SweepDirection = SweepDirection.Clockwise };
        _arcFigure = new PathFigure { IsClosed = false, IsFilled = false, Segments = { _arcSegment } };
        _arcGeometry = new PathGeometry { Figures = { _arcFigure } };
        var arc = new Shapes.Path
        {
            Data = _arcGeometry,
            StrokeThickness = ClockThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        Themed("AccentFillColorDefaultBrush", b => arc.Stroke = b);
        _clockArc = arc;
        canvas.Children.Add(arc);

        _clockStart = CreateHandle(TimeHandleRole.Start, InputSystemCursorShape.Hand);
        _clockEnd = CreateHandle(TimeHandleRole.End, InputSystemCursorShape.Hand);
        canvas.Children.Add(_clockStart);
        canvas.Children.Add(_clockEnd);

        LayoutClock();
    }

    private void OnClockSizeChanged(object sender, SizeChangedEventArgs e) => LayoutClock();

    private void LayoutClock()
    {
        if (!_clockBuilt || _clockCanvas is not { } canvas || _trackGeometry is null || _fullCircle is null ||
            _clockTicks is null || _arcSegment is null)
        {
            return;
        }

        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _clockX = width / 2;
        _clockY = height / 2;
        _clockRadius = Math.Max(10, Math.Min(width, height) / 2 - ClockMargin - ClockThickness / 2);
        var radius = _clockRadius;

        foreach (var circle in new[] { _trackGeometry, _fullCircle })
        {
            circle.Center = new Point(_clockX, _clockY);
            circle.RadiusX = radius;
            circle.RadiusY = radius;
        }

        _arcSegment.Size = new Size(radius, radius);

        // Tick marks just inside the ring: long at 6h, medium at 3h, short on the hour.
        var inner = radius - ClockThickness / 2 - 4;
        var group = new GeometryGroup();
        for (var hour = 0; hour < 24; hour++)
        {
            var length = hour % 6 == 0 ? 12 : hour % 3 == 0 ? 8 : 5;
            group.Children.Add(new LineGeometry
            {
                StartPoint = PointOnDial(hour * 15, inner),
                EndPoint = PointOnDial(hour * 15, inner - length),
            });
        }

        _clockTicks.Data = group;

        foreach (var (hour, text) in _clockLabels)
        {
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var at = PointOnDial(hour * 15, inner - 30);
            Canvas.SetLeft(text, at.X - text.DesiredSize.Width / 2);
            Canvas.SetTop(text, at.Y - text.DesiredSize.Height / 2);
        }

        UpdateClock(Value);
    }

    private void UpdateClock(TimeRangeValue value)
    {
        if (!_clockBuilt || _clockRadius <= 0 || _clockStart is null || _clockEnd is null ||
            _clockArc is null || _arcFigure is null || _arcSegment is null)
        {
            return;
        }

        var startAngle = TimeToDegrees(value.Start);
        PlaceHandle(_clockStart, PointOnDial(startAngle, _clockRadius));

        if (value.End is { } end)
        {
            var endAngle = TimeToDegrees(end);
            PlaceHandle(_clockEnd, PointOnDial(endAngle, _clockRadius));
            _clockEnd.Visibility = Visibility.Visible;

            var sweep = value.Duration.Ticks / (double)TimeRangeMath.Day.Ticks * 360;
            if (sweep >= 359.9)
            {
                // An arc cannot start and end at the same point, so a whole day is drawn as a full circle.
                _clockArc.Data = _fullCircle;
            }
            else
            {
                _arcFigure.StartPoint = PointOnDial(startAngle, _clockRadius);
                _arcSegment.Point = PointOnDial(endAngle, _clockRadius);
                _arcSegment.IsLargeArc = sweep > 180;
                _clockArc.Data = _arcGeometry;
            }

            _clockArc.Visibility = Visibility.Visible;
        }
        else
        {
            _clockEnd.Visibility = Visibility.Collapsed;
            _clockArc.Visibility = Visibility.Collapsed;
        }

        var culture = CultureInfo.CurrentCulture;
        var use24Hour = Use24Hour;
        if (_clockDuration is not null)
        {
            _clockDuration.Text = value.IsRange
                ? TimeRangeFormat.FormatDuration(value.Duration)
                : TimeRangeFormat.FormatTime(value.Start, culture, use24Hour);
        }

        if (_clockRange is not null)
        {
            _clockRange.Text = value.IsRange ? TimeRangeFormat.FormatRange(value, culture, use24Hour) : string.Empty;
        }

        UpdateHandleText(_clockStart, _clockEnd, value);
    }

    // --- Pointer input ---

    private void OnClockPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_clockBuilt || _clockCanvas is not { } canvas || _clockRadius <= 0 || _drag != DragKind.None)
        {
            return;
        }

        var point = e.GetCurrentPoint(canvas);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = point.Position;
        var fromCentre = Distance(position, new Point(_clockX, _clockY));
        if (Math.Abs(fromCentre - _clockRadius) > Math.Max(HandleReach, ClockThickness / 2 + 8))
        {
            return;
        }

        var time = ClockTimeAt(position);
        var (kind, jump) = HitTestClock(Value, position, time);

        FocusHandle(kind);
        BeginPointerDrag(kind, e.Pointer, canvas, time, wrap: true);
        if (jump)
        {
            // Pressing the bare ring sends the nearest handle straight there.
            _dragGrabOffset = TimeSpan.Zero;
            ContinueDrag(time);
        }

        e.Handled = true;
    }

    private (DragKind Kind, bool Jump) HitTestClock(TimeRangeValue value, Point position, TimeSpan time)
    {
        var startDistance = Distance(position, PointOnDial(TimeToDegrees(value.Start), _clockRadius));
        var endDistance = value.End is { } end
            ? Distance(position, PointOnDial(TimeToDegrees(end), _clockRadius))
            : double.MaxValue;

        if (Math.Min(startDistance, endDistance) <= HandleReach)
        {
            return (startDistance <= endDistance ? DragKind.Start : DragKind.End, false);
        }

        if (value.End is not { } endTime)
        {
            return (DragKind.Start, true);
        }

        if (TimeRangeMath.WrapToDay(time - value.Start) <= value.Duration)
        {
            return (DragKind.Range, false);
        }

        var startGap = TimeRangeMath.ShortestDelta(value.Start, time).Duration();
        var endGap = TimeRangeMath.ShortestDelta(endTime, time).Duration();
        return (startGap <= endGap ? DragKind.Start : DragKind.End, true);
    }

    private void OnClockPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == DragKind.None || e.Pointer.PointerId != _dragPointerId || _clockCanvas is not { } canvas)
        {
            return;
        }

        ContinueDrag(ClockTimeAt(e.GetCurrentPoint(canvas).Position));
    }

    private void OnClockPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (_drag != DragKind.None)
        {
            EndPointerDrag();
        }
    }
}

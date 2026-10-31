using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SetSett.Model;
using SetSett.Text;
using Windows.Foundation;
using Windows.System;
using Windows.System.UserProfile;
using Windows.UI.Core;

namespace SetSett.Controls;

public enum TimeRangePickerMode
{
    /// <summary>A day timeline with a draggable block, like a calendar event. It runs on into the next day for overnight blocks.</summary>
    Calendar,

    /// <summary>A 24-hour dial with start and end handles joined by an arc.</summary>
    Clock,
}

public sealed class TimeRangeChangedEventArgs(TimeRangeValue oldValue, TimeRangeValue newValue, bool isInteractive) : EventArgs
{
    public TimeRangeValue OldValue { get; } = oldValue;

    public TimeRangeValue NewValue { get; } = newValue;

    /// <summary>True while the user is editing (mid-drag included); false for changes made through the properties.</summary>
    public bool IsInteractive { get; } = isInteractive;
}

/// <summary>
/// Picks a range of time on a day. <see cref="Start"/> and <see cref="End"/> are offsets from midnight; an
/// <see cref="End"/> past 24h is an overnight span. The editor's "Time of day" row shows it as a dial
/// (<see cref="TimeRangePickerMode.Clock"/>), and each column of a <see cref="WeekSchedule"/> is one in
/// <see cref="TimeRangePickerMode.Calendar"/> mode.
/// </summary>
/// <remarks>
/// Ported from the Controls-Experiment lab without its compact view, mode switcher and point/range toggle, which Set
/// Sett doesn't use. A null <see cref="End"/> (a single point) still draws, but nothing here makes one.
/// </remarks>
public sealed partial class TimeRangePicker : Control
{
    public static readonly DependencyProperty StartProperty = DependencyProperty.Register(
        nameof(Start), typeof(TimeSpan), typeof(TimeRangePicker),
        new PropertyMetadata(TimeSpan.Zero, OnValueDependencyChanged));

    public static readonly DependencyProperty EndProperty = DependencyProperty.Register(
        nameof(End), typeof(TimeSpan?), typeof(TimeRangePicker),
        new PropertyMetadata(null, OnValueDependencyChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(TimeRangePickerMode), typeof(TimeRangePicker),
        new PropertyMetadata(TimeRangePickerMode.Calendar, (d, _) => ((TimeRangePicker)d).UpdateModeState()));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(TimeSpan), typeof(TimeRangePicker),
        new PropertyMetadata(TimeRangeConstraints.Default.Step, OnConstraintChanged));

    public static readonly DependencyProperty MinDurationProperty = DependencyProperty.Register(
        nameof(MinDuration), typeof(TimeSpan), typeof(TimeRangePicker),
        new PropertyMetadata(TimeRangeConstraints.Default.MinDuration, OnConstraintChanged));

    public static readonly DependencyProperty MaxDurationProperty = DependencyProperty.Register(
        nameof(MaxDuration), typeof(TimeSpan), typeof(TimeRangePicker),
        new PropertyMetadata(TimeRangeConstraints.Default.MaxDuration, OnConstraintChanged));

    /// <summary>Whether the editor shows the typed Start/End fields. Off for a calendar column that sits in a week view.</summary>
    public static readonly DependencyProperty ShowFieldsProperty = DependencyProperty.Register(
        nameof(ShowFields), typeof(bool), typeof(TimeRangePicker),
        new PropertyMetadata(true, (d, _) => ((TimeRangePicker)d).UpdateVisuals()));

    /// <summary>Whether the calendar timeline shows its hour labels. A week view draws one shared axis instead.</summary>
    public static readonly DependencyProperty ShowTimeAxisProperty = DependencyProperty.Register(
        nameof(ShowTimeAxis), typeof(bool), typeof(TimeRangePicker),
        new PropertyMetadata(true, (d, _) => ((TimeRangePicker)d).InvalidateSurfaces()));

    /// <summary>Height of the calendar timeline's viewport. One pixel is one minute, so 1500 or more shows a whole day at once.</summary>
    public static readonly DependencyProperty CalendarHeightProperty = DependencyProperty.Register(
        nameof(CalendarHeight), typeof(double), typeof(TimeRangePicker), new PropertyMetadata(320d));

    private static bool? _systemUses24Hour;

    private bool _isApplying;
    private TimeRangeValue _announced;
    private TimeRangeValue _lastCommitted;
    private TimeRangeValue _session;
    private readonly List<(string Key, Action<Brush> Apply)> _themed = [];

    private FrameworkElement? _fields;
    private TextBox? _startBox;
    private TextBox? _endBox;
    private FrameworkElement? _rangeArrow;
    private FrameworkElement? _endPanel;
    private TextBlock? _endHint;
    private ScrollViewer? _calendarScroller;
    private Canvas? _calendarCanvas;
    private Canvas? _clockCanvas;
    private TextBlock? _clockDuration;
    private TextBlock? _clockRange;

    private DragKind _drag;
    private TimeSpan _dragGrabOffset;
    private bool _dragWraps;
    private UIElement? _dragSurface;
    private uint _dragPointerId;

    public TimeRangePicker()
    {
        DefaultStyleKey = typeof(TimeRangePicker);
        Loaded += OnLoaded;
        ActualThemeChanged += (_, _) => RefreshThemedBrushes();
    }

    /// <summary>Raised on every change of <see cref="Start"/>/<see cref="End"/>, live while dragging.</summary>
    public event TypedEventHandler<TimeRangePicker, TimeRangeChangedEventArgs>? ValueChanged;

    /// <summary>Raised once when an edit finishes (pointer released, key pressed, text entered). Save on this one.</summary>
    public event TypedEventHandler<TimeRangePicker, TimeRangeChangedEventArgs>? ValueCommitted;

    /// <summary>Whether times show as 24-hour, following the Windows clock setting (read once).</summary>
    public static bool Use24Hour => _systemUses24Hour ??= SystemUses24Hour();

    public TimeSpan Start
    {
        get => (TimeSpan)GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    /// <summary>Null for a single point in time.</summary>
    public TimeSpan? End
    {
        get => (TimeSpan?)GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    public TimeRangePickerMode Mode
    {
        get => (TimeRangePickerMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public TimeSpan Step
    {
        get => (TimeSpan)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public TimeSpan MinDuration
    {
        get => (TimeSpan)GetValue(MinDurationProperty);
        set => SetValue(MinDurationProperty, value);
    }

    public TimeSpan MaxDuration
    {
        get => (TimeSpan)GetValue(MaxDurationProperty);
        set => SetValue(MaxDurationProperty, value);
    }

    public bool ShowFields
    {
        get => (bool)GetValue(ShowFieldsProperty);
        set => SetValue(ShowFieldsProperty, value);
    }

    public bool ShowTimeAxis
    {
        get => (bool)GetValue(ShowTimeAxisProperty);
        set => SetValue(ShowTimeAxisProperty, value);
    }

    public double CalendarHeight
    {
        get => (double)GetValue(CalendarHeightProperty);
        set => SetValue(CalendarHeightProperty, value);
    }

    /// <summary>
    /// <see cref="Start"/> and <see cref="End"/> as one value, snapped and repaired against the constraints. Setting it
    /// changes both properties at once. Out-of-range raw property values are shown repaired but not rewritten until the
    /// user edits, so the order in which XAML sets the two never matters.
    /// </summary>
    public TimeRangeValue Value
    {
        get => TimeRangeMath.Normalize(new(Start, End), Constraints);
        set => ApplyValue(value, isInteractive: false);
    }

    private TimeRangeConstraints Constraints => new TimeRangeConstraints(Step, MinDuration, MaxDuration).Sanitize();

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        DetachParts();

        _fields = GetTemplateChild("PART_Fields") as FrameworkElement;
        _startBox = GetTemplateChild("PART_StartBox") as TextBox;
        _endBox = GetTemplateChild("PART_EndBox") as TextBox;
        _rangeArrow = GetTemplateChild("PART_RangeArrow") as FrameworkElement;
        _endPanel = GetTemplateChild("PART_EndPanel") as FrameworkElement;
        _endHint = GetTemplateChild("PART_EndHint") as TextBlock;
        _calendarScroller = GetTemplateChild("PART_CalendarScroller") as ScrollViewer;
        _calendarCanvas = GetTemplateChild("PART_CalendarCanvas") as Canvas;
        _clockCanvas = GetTemplateChild("PART_ClockCanvas") as Canvas;
        _clockDuration = GetTemplateChild("PART_ClockDuration") as TextBlock;
        _clockRange = GetTemplateChild("PART_ClockRange") as TextBlock;

        AttachParts();
        UpdateModeState();
    }

    private void AttachParts()
    {
        // Preview, because a TextBox consumes keys like Down before a plain KeyDown handler would see them.
        if (_startBox is not null)
        {
            _startBox.PreviewKeyDown += OnStartBoxKeyDown;
            _startBox.LostFocus += OnStartBoxLostFocus;
        }

        if (_endBox is not null)
        {
            _endBox.PreviewKeyDown += OnEndBoxKeyDown;
            _endBox.LostFocus += OnEndBoxLostFocus;
        }

        if (_calendarScroller is not null)
        {
            _calendarScroller.SizeChanged += OnCalendarSizeChanged;
        }

        if (_calendarCanvas is not null)
        {
            _calendarCanvas.PointerPressed += OnCalendarPointerPressed;
            _calendarCanvas.PointerMoved += OnCalendarPointerMoved;
            _calendarCanvas.PointerReleased += OnCalendarPointerReleased;
            _calendarCanvas.PointerCanceled += OnCalendarPointerCanceled;
            _calendarCanvas.PointerCaptureLost += OnCalendarPointerCanceled;
        }

        if (_clockCanvas is not null)
        {
            // Claim touch drags so a parent ScrollViewer does not pan the page while the dial is being turned.
            _clockCanvas.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
            _clockCanvas.SizeChanged += OnClockSizeChanged;
            _clockCanvas.PointerPressed += OnClockPointerPressed;
            _clockCanvas.PointerMoved += OnClockPointerMoved;
            _clockCanvas.PointerReleased += OnClockPointerEnded;
            _clockCanvas.PointerCanceled += OnClockPointerEnded;
            _clockCanvas.PointerCaptureLost += OnClockPointerEnded;
        }
    }

    private void DetachParts()
    {
        if (_startBox is not null)
        {
            _startBox.PreviewKeyDown -= OnStartBoxKeyDown;
            _startBox.LostFocus -= OnStartBoxLostFocus;
        }

        if (_endBox is not null)
        {
            _endBox.PreviewKeyDown -= OnEndBoxKeyDown;
            _endBox.LostFocus -= OnEndBoxLostFocus;
        }

        if (_calendarScroller is not null)
        {
            _calendarScroller.SizeChanged -= OnCalendarSizeChanged;
        }

        if (_calendarCanvas is not null)
        {
            _calendarCanvas.PointerPressed -= OnCalendarPointerPressed;
            _calendarCanvas.PointerMoved -= OnCalendarPointerMoved;
            _calendarCanvas.PointerReleased -= OnCalendarPointerReleased;
            _calendarCanvas.PointerCanceled -= OnCalendarPointerCanceled;
            _calendarCanvas.PointerCaptureLost -= OnCalendarPointerCanceled;
        }

        if (_clockCanvas is not null)
        {
            _clockCanvas.SizeChanged -= OnClockSizeChanged;
            _clockCanvas.PointerPressed -= OnClockPointerPressed;
            _clockCanvas.PointerMoved -= OnClockPointerMoved;
            _clockCanvas.PointerReleased -= OnClockPointerEnded;
            _clockCanvas.PointerCanceled -= OnClockPointerEnded;
            _clockCanvas.PointerCaptureLost -= OnClockPointerEnded;
        }

        _calendarBuilt = false;
        _clockBuilt = false;
        _themed.Clear();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _session = _lastCommitted = Value;
    }

    private static void OnValueDependencyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (TimeRangePicker)d;
        if (!picker._isApplying)
        {
            picker.OnValueChanged(isInteractive: false);
        }
    }

    private static void OnConstraintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimeRangePicker)d).OnValueChanged(isInteractive: false);

    private void ApplyValue(TimeRangeValue value, bool isInteractive)
    {
        value = TimeRangeMath.Normalize(value, Constraints);
        _isApplying = true;
        try
        {
            Start = value.Start;
            End = value.End;
        }
        finally
        {
            _isApplying = false;
        }

        OnValueChanged(isInteractive);
    }

    private void OnValueChanged(bool isInteractive)
    {
        var next = Value;
        if (!isInteractive)
        {
            _lastCommitted = next;
        }

        UpdateVisuals();
        if (next == _announced)
        {
            return;
        }

        var previous = _announced;
        _announced = next;
        ValueChanged?.Invoke(this, new(previous, next, isInteractive));
    }

    private void Commit()
    {
        var value = Value;
        if (value == _lastCommitted)
        {
            return;
        }

        var previous = _lastCommitted;
        _lastCommitted = value;
        ValueCommitted?.Invoke(this, new(previous, value, isInteractive: true));
    }

    private void RevertSession()
    {
        if (Value == _session)
        {
            return;
        }

        ApplyValue(_session, isInteractive: true);
        Commit();
    }

    /// <summary>Enter: the value so far becomes what Escape goes back to.</summary>
    private void Complete()
    {
        _session = Value;
        Commit();
    }

    private void UpdateModeState()
    {
        VisualStateManager.GoToState(this, Mode.ToString(), false);

        switch (Mode)
        {
            case TimeRangePickerMode.Calendar:
                EnsureCalendar();
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollCalendarToValue);
                break;
            case TimeRangePickerMode.Clock:
                EnsureClock();
                break;
        }

        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var value = Value;

        if (_fields is not null)
        {
            _fields.Visibility = ShowFields ? Visibility.Visible : Visibility.Collapsed;
        }

        var endVisibility = value.IsRange ? Visibility.Visible : Visibility.Collapsed;
        if (_endPanel is not null)
        {
            _endPanel.Visibility = endVisibility;
        }

        if (_rangeArrow is not null)
        {
            _rangeArrow.Visibility = endVisibility;
        }

        if (_endHint is not null)
        {
            _endHint.Visibility = value.SpansMidnight ? Visibility.Visible : Visibility.Collapsed;
        }

        RefreshFields(force: false);

        UpdateCalendar(value);
        UpdateClock(value);
    }

    /// <summary>Shows the canonical text. A focused box is left alone (the user may be typing) unless <paramref name="force"/>.</summary>
    private static void RefreshField(TextBox? box, TimeSpan? timeOfDay, CultureInfo culture, bool force = false)
    {
        if (box is null || (!force && box.FocusState != FocusState.Unfocused))
        {
            return;
        }

        box.Text = timeOfDay is { } t ? TimeRangeFormat.FormatTime(t, culture, Use24Hour) : string.Empty;
    }

    private void InvalidateSurfaces()
    {
        _calendarCanvas?.Children.Clear();
        _clockCanvas?.Children.Clear();
        _calendarBuilt = false;
        _clockBuilt = false;
        _themed.Clear();
        UpdateModeState();
    }

    // --- Typed fields ---

    private void OnStartBoxKeyDown(object sender, KeyRoutedEventArgs e) => OnFieldKeyDown(isStart: true, e);

    private void OnEndBoxKeyDown(object sender, KeyRoutedEventArgs e) => OnFieldKeyDown(isStart: false, e);

    private void OnStartBoxLostFocus(object sender, RoutedEventArgs e) => CommitField(isStart: true);

    private void OnEndBoxLostFocus(object sender, RoutedEventArgs e) => CommitField(isStart: false);

    private void OnFieldKeyDown(bool isStart, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                CommitField(isStart);
                Complete();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                RevertSession();
                RefreshFields(force: true);
                break;
            case VirtualKey.Up or VirtualKey.Down:
                // Fields behave like spinners: Up is later.
                CommitField(isStart);
                var step = e.Key == VirtualKey.Up ? Constraints.Step : -Constraints.Step;
                Nudge(isStart ? TimeHandleRole.Start : TimeHandleRole.End, step, wholeRange: isStart, wrap: false);
                RefreshFields(force: true);
                e.Handled = true;
                break;
        }
    }

    private void RefreshFields(bool force)
    {
        var value = Value;
        var culture = CultureInfo.CurrentCulture;
        RefreshField(_startBox, value.Start, culture, force);
        RefreshField(_endBox, value.EndTimeOfDay, culture, force);
    }

    private void CommitField(bool isStart)
    {
        var box = isStart ? _startBox : _endBox;
        if (box is null)
        {
            return;
        }

        if (ApplyTypedTime(isStart, box.Text))
        {
            Commit();
        }

        // Always show the canonical text again; it also undoes input that did not parse.
        RefreshField(box, isStart ? Value.Start : Value.EndTimeOfDay, CultureInfo.CurrentCulture, force: true);
    }

    /// <summary>A typed start slides the whole range (like editing an event); a typed end resizes it, past midnight if needed.</summary>
    private bool ApplyTypedTime(bool isStart, string text)
    {
        if (!TimeRangeFormat.TryParseTime(text, out var timeOfDay))
        {
            return false;
        }

        var value = Value;
        TimeRangeValue next;
        if (isStart)
        {
            next = TimeRangeMath.MoveRange(value, timeOfDay - value.Start, Constraints, wrap: false);
        }
        else if (value.End is { } end && TimeRangeMath.ResolveEnd(value.Start, timeOfDay) is { } resolved)
        {
            next = TimeRangeMath.MoveEnd(value, resolved - end, Constraints);
        }
        else
        {
            return false;
        }

        if (next == value)
        {
            return false;
        }

        ApplyValue(next, isInteractive: true);
        return true;
    }

    // --- Keyboard on the handles ---

    private void OnHandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var handle = (TimeHandle)sender;
        var large = IsKeyDown(VirtualKey.Shift);
        var step = Constraints.Step;

        TimeSpan? delta = e.Key switch
        {
            VirtualKey.Up or VirtualKey.Left => large ? -step * 4 : -step,
            VirtualKey.Down or VirtualKey.Right => large ? step * 4 : step,
            VirtualKey.PageUp => -TimeSpan.FromHours(1),
            VirtualKey.PageDown => TimeSpan.FromHours(1),
            _ => null,
        };

        if (delta is { } d)
        {
            Nudge(handle.Role, d, wholeRange: IsKeyDown(VirtualKey.Control), wrap: Mode == TimeRangePickerMode.Clock);
            handle.StartBringIntoView();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            Complete();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            RevertSession();
        }
    }

    private void Nudge(TimeHandleRole role, TimeSpan delta, bool wholeRange, bool wrap)
    {
        var value = Value;
        var constraints = Constraints;
        var next = wholeRange
            ? TimeRangeMath.MoveRange(value, delta, constraints, wrap)
            : role == TimeHandleRole.Start
                ? TimeRangeMath.MoveStart(value, delta, constraints, wrap)
                : TimeRangeMath.MoveEnd(value, delta, constraints);
        ApplyValue(next, isInteractive: true);
        Commit();
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // --- Pointer dragging, shared by both surfaces ---

    private enum DragKind
    {
        None,
        Start,
        End,
        Range,
    }

    /// <summary>
    /// Starts a drag. The picker tracks where on the edge the pointer grabbed (the "grab offset") so the edge does not
    /// jump to the pointer; <paramref name="wrap"/> is true on the dial, where times go round instead of stopping.
    /// </summary>
    private void BeginPointerDrag(DragKind kind, Pointer pointer, UIElement surface, TimeSpan pointerTime, bool wrap)
    {
        var anchor = DragAnchor(Value, kind);
        _drag = kind;
        _dragWraps = wrap;
        _dragGrabOffset = wrap ? TimeRangeMath.ShortestDelta(anchor, pointerTime) : pointerTime - anchor;
        _dragSurface = surface;
        _dragPointerId = pointer.PointerId;
        surface.CapturePointer(pointer);

        if (!wrap && _calendarScroller is not null)
        {
            _calendarScroller.VerticalScrollMode = ScrollMode.Disabled;
        }
    }

    private void ContinueDrag(TimeSpan pointerTime)
    {
        if (_drag == DragKind.None)
        {
            return;
        }

        var value = Value;
        var constraints = Constraints;
        var target = pointerTime - _dragGrabOffset;
        var anchor = DragAnchor(value, _drag);
        var delta = _dragWraps ? TimeRangeMath.ShortestDelta(anchor, target) : target - anchor;

        var next = _drag switch
        {
            DragKind.Start => TimeRangeMath.MoveStart(value, delta, constraints, _dragWraps),
            DragKind.End => TimeRangeMath.MoveEnd(value, delta, constraints),
            _ => TimeRangeMath.MoveRange(value, delta, constraints, _dragWraps),
        };

        if (next != value)
        {
            ApplyValue(next, isInteractive: true);
        }
    }

    private void EndPointerDrag()
    {
        var surface = _dragSurface;
        _drag = DragKind.None;
        _dragSurface = null;
        StopAutoScroll();
        surface?.ReleasePointerCaptures();

        if (_calendarScroller is not null)
        {
            _calendarScroller.VerticalScrollMode = ScrollMode.Auto;
        }

        SetBlockLifted(false);
        UpdateVisuals();
        Commit();
    }

    private static TimeSpan DragAnchor(TimeRangeValue value, DragKind kind) =>
        kind == DragKind.End ? value.End ?? value.Start : value.Start;

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    // --- Handles and theming ---

    private TimeHandle CreateHandle(TimeHandleRole role, InputSystemCursorShape cursor)
    {
        var handle = new TimeHandle { Role = role };
        handle.SetCursor(cursor);

        // Claim touch drags that start on the handle. Without this a parent ScrollViewer (the calendar timeline) turns the
        // same touch into a pan before any pointer capture can stop it. The pointer events still bubble to the surface.
        handle.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        AutomationProperties.SetName(handle,role == TimeHandleRole.Start ? "Start time" : "End time");
        handle.KeyDown += OnHandleKeyDown;
        handle.TextCommitted = text =>
        {
            var applied = ApplyTypedTime(role == TimeHandleRole.Start, text);
            if (applied)
            {
                Commit();
            }

            return applied;
        };
        return handle;
    }

    private void FocusHandle(DragKind kind)
    {
        var handle = kind == DragKind.End
            ? Mode == TimeRangePickerMode.Clock ? _clockEnd : _calendarEnd
            : Mode == TimeRangePickerMode.Clock ? _clockStart : _calendarStart;
        handle?.Focus(FocusState.Pointer);
    }

    private static void UpdateHandleText(TimeHandle? start, TimeHandle? end, TimeRangeValue value)
    {
        var culture = CultureInfo.CurrentCulture;
        if (start is not null)
        {
            start.ValueText = TimeRangeFormat.FormatTime(value.Start, culture, Use24Hour);
        }

        if (end is not null && value.EndTimeOfDay is { } endOfDay)
        {
            var text = TimeRangeFormat.FormatTime(endOfDay, culture, Use24Hour);
            end.ValueText = value.SpansMidnight ? $"{text}, next day" : text;
        }
    }

    /// <summary>
    /// Brushes for elements created in code come from the theme resources, and are looked up again when the theme changes.
    /// </summary>
    private void Themed(string key, Action<Brush> apply)
    {
        _themed.Add((key, apply));
        apply(ThemeBrush(key));
    }

    private void RefreshThemedBrushes()
    {
        foreach (var (key, apply) in _themed)
        {
            apply(ThemeBrush(key));
        }
    }

    internal static Brush ThemeBrush(string key)
    {
        try
        {
            return (Brush)Application.Current.Resources[key];
        }
        catch (Exception)
        {
            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private static bool SystemUses24Hour()
    {
        try
        {
            if (GlobalizationPreferences.Clocks.FirstOrDefault() is { } clock)
            {
                return clock == "24HourClock";
            }
        }
        catch (Exception)
        {
            // Fall through to the culture's pattern.
        }

        return TimeRangeFormat.Is24Hour(CultureInfo.CurrentCulture);
    }
}

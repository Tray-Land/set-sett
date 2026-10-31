using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SetSett.Model;
using SetSett.Text;
using Windows.Foundation;

namespace SetSett.Controls;

/// <summary>One day of a <see cref="WeekSchedule"/>: whether it is on, and the range of time picked for it.</summary>
public sealed partial class DaySchedule : INotifyPropertyChanged
{
    private bool _isOn;
    private TimeRangeValue _value;

    internal DaySchedule(DayOfWeek day, bool isOn, TimeRangeValue value)
    {
        Day = day;
        _isOn = isOn;
        _value = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DayOfWeek Day { get; }

    /// <summary>A day that is off keeps its range, so switching it back on restores what was there.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn != value)
            {
                _isOn = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
            }
        }
    }

    public TimeRangeValue Value
    {
        get => _value;
        set
        {
            if (_value != value)
            {
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
    }
}

/// <summary>
/// A week shown as seven day columns. Each column is a calendar-mode <see cref="TimeRangePicker"/> on a shared hour axis,
/// with a switch above it to turn the day on or off. The columns scroll together.
/// </summary>
/// <remarks>Ported from the Controls-Experiment lab; it follows the Windows clock setting instead of taking a format.</remarks>
public sealed partial class WeekSchedule : Control
{
    private const double AxisWidth = 56;
    private static readonly TimeSpan MorningScroll = TimeSpan.FromHours(6);
    private static readonly TimeSpan ScrollContext = TimeSpan.FromHours(1);

    public static readonly DependencyProperty FirstDayOfWeekProperty = DependencyProperty.Register(
        nameof(FirstDayOfWeek), typeof(DayOfWeek), typeof(WeekSchedule),
        new PropertyMetadata(CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek, (d, _) => ((WeekSchedule)d).Rebuild()));

    /// <summary>Height of the scrolling area under the day headers.</summary>
    public static readonly DependencyProperty ViewportHeightProperty = DependencyProperty.Register(
        nameof(ViewportHeight), typeof(double), typeof(WeekSchedule), new PropertyMetadata(560d));

    /// <summary>Narrowest a day column gets; below that the week scrolls sideways.</summary>
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(WeekSchedule),
        new PropertyMetadata(80d, (d, _) => ((WeekSchedule)d).UpdateContentWidth()));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(TimeSpan), typeof(WeekSchedule),
        new PropertyMetadata(TimeRangeConstraints.Default.Step, (d, _) => ((WeekSchedule)d).ApplyOptions()));

    public static readonly DependencyProperty MinDurationProperty = DependencyProperty.Register(
        nameof(MinDuration), typeof(TimeSpan), typeof(WeekSchedule),
        new PropertyMetadata(TimeRangeConstraints.Default.MinDuration, (d, _) => ((WeekSchedule)d).ApplyOptions()));

    public static readonly DependencyProperty MaxDurationProperty = DependencyProperty.Register(
        nameof(MaxDuration), typeof(TimeSpan), typeof(WeekSchedule),
        new PropertyMetadata(TimeRangeConstraints.Default.MaxDuration, (d, _) => ((WeekSchedule)d).ApplyOptions()));

    private readonly DaySchedule[] _days;
    private readonly List<Column> _columns = [];
    private bool _syncing;

    private ScrollViewer? _horizontalScroller;
    private Grid? _content;
    private Grid? _headerGrid;
    private ScrollViewer? _verticalScroller;
    private Grid? _bodyGrid;

    public WeekSchedule()
    {
        DefaultStyleKey = typeof(WeekSchedule);
        ActualThemeChanged += (_, _) => Rebuild();

        // Monday to Friday on, 9 to 5; the weekend off but ready with the same hours. Callers usually overwrite these.
        _days = new DaySchedule[7];
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)i;
            var weekday = day is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            _days[i] = new DaySchedule(day, weekday, new TimeRangeValue(TimeSpan.FromHours(9), TimeSpan.FromHours(17)));
        }
    }

    /// <summary>Raised once when an edit to a day finishes (a drag released, a key pressed) or a day is switched on or off.</summary>
    public event TypedEventHandler<WeekSchedule, DaySchedule>? DayCommitted;

    /// <summary>The seven days, indexed by <see cref="DayOfWeek"/> (Sunday is 0) whatever order they are shown in.</summary>
    public IReadOnlyList<DaySchedule> Days => _days;

    public DayOfWeek FirstDayOfWeek
    {
        get => (DayOfWeek)GetValue(FirstDayOfWeekProperty);
        set => SetValue(FirstDayOfWeekProperty, value);
    }

    public double ViewportHeight
    {
        get => (double)GetValue(ViewportHeightProperty);
        set => SetValue(ViewportHeightProperty, value);
    }

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
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

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_horizontalScroller is not null)
        {
            _horizontalScroller.SizeChanged -= OnScrollerSizeChanged;
        }

        _horizontalScroller = GetTemplateChild("PART_HorizontalScroller") as ScrollViewer;
        _content = GetTemplateChild("PART_Content") as Grid;
        _headerGrid = GetTemplateChild("PART_HeaderGrid") as Grid;
        _verticalScroller = GetTemplateChild("PART_VerticalScroller") as ScrollViewer;
        _bodyGrid = GetTemplateChild("PART_BodyGrid") as Grid;

        if (_horizontalScroller is not null)
        {
            _horizontalScroller.SizeChanged += OnScrollerSizeChanged;
        }

        Rebuild();
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateContentWidth();

    /// <summary>The week fills the width it is given, but never gets narrower than its columns need; then it scrolls sideways.</summary>
    private void UpdateContentWidth()
    {
        if (_content is null || _horizontalScroller is null)
        {
            return;
        }

        _content.Width = Math.Max(_horizontalScroller.ActualWidth, AxisWidth + 7 * MinColumnWidth);
    }

    private void ApplyOptions()
    {
        foreach (var column in _columns)
        {
            column.Picker.Step = Step;
            column.Picker.MinDuration = MinDuration;
            column.Picker.MaxDuration = MaxDuration;
        }
    }

    private void Rebuild()
    {
        if (_headerGrid is null || _bodyGrid is null)
        {
            return;
        }

        foreach (var column in _columns)
        {
            column.Detach();
        }

        _columns.Clear();
        _headerGrid.Children.Clear();
        _headerGrid.ColumnDefinitions.Clear();
        _bodyGrid.Children.Clear();
        _bodyGrid.ColumnDefinitions.Clear();

        // Header and body share the same column layout, so the switches sit over their columns.
        foreach (var grid in new[] { _headerGrid, _bodyGrid })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AxisWidth) });
            for (var i = 0; i < 7; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
        }

        BuildAxis(_bodyGrid);

        var names = CultureInfo.CurrentCulture.DateTimeFormat;
        for (var i = 0; i < 7; i++)
        {
            var day = _days[((int)FirstDayOfWeek + i) % 7];
            var column = new Column(this, day, names.GetAbbreviatedDayName(day.Day), names.GetDayName(day.Day));
            column.Attach(_headerGrid, _bodyGrid, i + 1);
            _columns.Add(column);
        }

        UpdateContentWidth();
        ScrollToFirstStart(attempts: 5);
    }

    /// <summary>One shared column of hour labels, in line with the pickers' own hour lines.</summary>
    private static void BuildAxis(Grid body)
    {
        var use24Hour = TimeRangePicker.Use24Hour;
        var culture = CultureInfo.CurrentCulture;

        // The pickers sit inside a 1px border, so the axis does too, to keep the labels on their lines.
        var axis = new Canvas
        {
            Height = TimeRangePicker.CalendarFullHeight - 2,
            Margin = new Thickness(0, 1, 0, 1),
            IsHitTestVisible = false,
        };

        for (var hour = 0; hour <= 48; hour++)
        {
            var label = new TextBlock
            {
                Text = TimeRangeFormat.FormatHour(hour, culture, use24Hour),
                Width = AxisWidth - 10,
                TextAlignment = TextAlignment.Right,
                FontSize = 12,
                Foreground = TimeRangePicker.ThemeBrush("TextFillColorSecondaryBrush"),
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, TimeRangePicker.CalendarAxisY(hour * 60) - 9);
            axis.Children.Add(label);
        }

        var nextDay = new TextBlock
        {
            Text = "Next day",
            Width = AxisWidth - 10,
            TextAlignment = TextAlignment.Right,
            FontSize = 11,
            Foreground = TimeRangePicker.ThemeBrush("TextFillColorTertiaryBrush"),
        };
        Canvas.SetLeft(nextDay, 0);
        Canvas.SetTop(nextDay, TimeRangePicker.CalendarAxisY(24 * 60) + 10);
        axis.Children.Add(nextDay);

        Grid.SetColumn(axis, 0);
        body.Children.Add(axis);
    }

    /// <summary>
    /// Opens on the earliest start among the days that are on, with an hour of context above it, so a late block (a
    /// night window starting at 10 PM) is in view without scrolling. With every day off it opens on the morning.
    /// </summary>
    private void ScrollToFirstStart(int attempts)
    {
        if (_verticalScroller is not { } scroller || attempts <= 0)
        {
            return;
        }

        var on = _days.Where(d => d.IsOn).Select(d => d.Value.Start).ToList();
        var target = on.Count == 0 ? MorningScroll : on.Min() - ScrollContext;
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            // The content has no height until the first layout pass has run; try again until it does.
            if (scroller.ScrollableHeight <= 0)
            {
                ScrollToFirstStart(attempts - 1);
                return;
            }

            scroller.ChangeView(null, TimeRangePicker.CalendarAxisY(target.TotalMinutes), null, disableAnimation: true);
        });
    }

    /// <summary>The header (name and switch) and the picker for one day, kept in step with its <see cref="DaySchedule"/>.</summary>
    private sealed class Column
    {
        private readonly WeekSchedule _owner;
        private readonly DaySchedule _day;
        private readonly TextBlock _name;
        private readonly ToggleSwitch _switch;

        public Column(WeekSchedule owner, DaySchedule day, string shortName, string fullName)
        {
            _owner = owner;
            _day = day;

            _name = new TextBlock { Text = shortName, HorizontalAlignment = HorizontalAlignment.Center };

            _switch = new ToggleSwitch
            {
                IsOn = day.IsOn,
                OnContent = string.Empty,
                OffContent = string.Empty,
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            AutomationProperties.SetName(_switch, $"{fullName} on");

            Picker = new TimeRangePicker
            {
                Mode = TimeRangePickerMode.Calendar,
                ShowFields = false,
                ShowTimeAxis = false,
                CalendarHeight = TimeRangePicker.CalendarFullHeight,
                Step = owner.Step,
                MinDuration = owner.MinDuration,
                MaxDuration = owner.MaxDuration,
                Margin = new Thickness(2, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(Picker, fullName);

            // A point cannot be edited here (there is no Range switch), so every day holds a range.
            if (!day.Value.IsRange)
            {
                day.Value = TimeRangeMath.ToRange(day.Value, TimeRangeConstraints.Default);
            }

            Picker.Value = day.Value;
            ShowState();
        }

        public TimeRangePicker Picker { get; }

        public void Attach(Grid header, Grid body, int gridColumn)
        {
            var headerCell = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
            headerCell.Children.Add(_name);
            headerCell.Children.Add(_switch);
            Grid.SetColumn(headerCell, gridColumn);
            header.Children.Add(headerCell);

            Grid.SetColumn(Picker, gridColumn);
            body.Children.Add(Picker);

            _switch.Toggled += OnSwitchToggled;
            Picker.ValueChanged += OnPickerValueChanged;
            Picker.ValueCommitted += OnPickerValueCommitted;
            _day.PropertyChanged += OnDayPropertyChanged;
        }

        public void Detach()
        {
            _switch.Toggled -= OnSwitchToggled;
            Picker.ValueChanged -= OnPickerValueChanged;
            Picker.ValueCommitted -= OnPickerValueCommitted;
            _day.PropertyChanged -= OnDayPropertyChanged;
        }

        /// <summary>A day that is off is dimmed and ignores input, but still shows (and keeps) its range.</summary>
        private void ShowState()
        {
            Picker.IsEnabled = _day.IsOn;
            Picker.IsHitTestVisible = _day.IsOn;
            Picker.Opacity = _day.IsOn ? 1 : 0.4;
            _name.Opacity = _day.IsOn ? 1 : 0.6;
            if (_switch.IsOn != _day.IsOn)
            {
                _switch.IsOn = _day.IsOn;
            }
        }

        private void OnSwitchToggled(object sender, RoutedEventArgs e)
        {
            if (_day.IsOn == _switch.IsOn)
            {
                return;
            }

            _day.IsOn = _switch.IsOn;
            _owner.DayCommitted?.Invoke(_owner, _day);
        }

        private void OnPickerValueChanged(TimeRangePicker sender, TimeRangeChangedEventArgs args)
        {
            _owner._syncing = true;
            _day.Value = args.NewValue;
            _owner._syncing = false;
        }

        private void OnPickerValueCommitted(TimeRangePicker sender, TimeRangeChangedEventArgs args) =>
            _owner.DayCommitted?.Invoke(_owner, _day);

        private void OnDayPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DaySchedule.IsOn))
            {
                ShowState();
            }
            else if (e.PropertyName == nameof(DaySchedule.Value) && !_owner._syncing && Picker.Value != _day.Value)
            {
                Picker.Value = _day.Value;
            }
        }
    }
}

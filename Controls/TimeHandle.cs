using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace SetSett.Controls;

public enum TimeHandleRole
{
    Start,
    End,
}

/// <summary>
/// The grip for one end of a <see cref="TimeRangePicker"/> range. It supplies the visuals, keyboard focus and automation;
/// the picker owns dragging and keys so both surfaces (calendar and clock) behave identically.
/// </summary>
public sealed partial class TimeHandle : Control
{
    public static readonly DependencyProperty RoleProperty = DependencyProperty.Register(
        nameof(Role), typeof(TimeHandleRole), typeof(TimeHandle),
        new PropertyMetadata(TimeHandleRole.Start, (d, _) => ((TimeHandle)d).UpdateVisualState()));

    /// <summary>The time the handle sits on, as text, for assistive technology.</summary>
    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(TimeHandle),
        new PropertyMetadata(string.Empty, OnValueTextChanged));

    public TimeHandle()
    {
        DefaultStyleKey = typeof(TimeHandle);
    }

    public TimeHandleRole Role
    {
        get => (TimeHandleRole)GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    /// <summary>Lets assistive technology type a time into the handle; returns false when the text is not a time.</summary>
    internal Func<string, bool>? TextCommitted { get; set; }

    public void SetCursor(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateVisualState();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TimeHandleAutomationPeer(this);

    private void UpdateVisualState() => VisualStateManager.GoToState(this, Role.ToString(), true);

    private static void OnValueTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged) &&
            FrameworkElementAutomationPeer.FromElement((TimeHandle)d) is TimeHandleAutomationPeer peer)
        {
            peer.RaiseValueChanged((string)e.OldValue, (string)e.NewValue);
        }
    }
}

public sealed partial class TimeHandleAutomationPeer(TimeHandle owner) : FrameworkElementAutomationPeer(owner), IValueProvider
{
    public bool IsReadOnly => false;

    public string Value => owner.ValueText;

    public void SetValue(string value) => owner.TextCommitted?.Invoke(value);

    protected override string GetClassNameCore() => nameof(TimeHandle);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);

    internal void RaiseValueChanged(string oldValue, string newValue) =>
        RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, oldValue, newValue);
}

/// <summary>A <see cref="Grid"/> that can ask for a cursor (the framework only exposes that to subclasses).</summary>
internal sealed partial class CursorBorder : Grid
{
    public void SetCursor(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);
}

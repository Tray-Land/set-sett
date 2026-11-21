using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SetSett.Engine;
using static SetSett.Views.EditorParts;

namespace SetSett.Views;

/// <summary>A compact, labeled status that remains legible without relying on color.</summary>
internal static class StatusIndicator
{
    public static TextBlock Create() => Caption("");

    public static void Set(TextBlock indicator, ConditionStatus status)
    {
        (string symbol, string label) = status switch
        {
            ConditionStatus.Active => ("●", "Active"),
            ConditionStatus.Inactive => ("○", "Inactive"),
            ConditionStatus.Unknown => ("–", "Unknown"),
            ConditionStatus.NotMonitored => ("–", "Not monitored"),
            ConditionStatus.Instant => ("–", "Instant"),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        Set(indicator, symbol, label, status == ConditionStatus.Active);
    }

    public static void SetSetter(TextBlock indicator, bool active, bool toggle) =>
        Set(indicator, toggle ? "–" : active ? "●" : "○",
            toggle ? "One-click action" : active ? "Active" : "Inactive", active && !toggle);

    private static void Set(TextBlock indicator, string symbol, string label, bool active)
    {
        indicator.Text = $"{symbol}  {label}";
        indicator.Foreground = Brush(active ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
        AutomationProperties.SetName(indicator, label);
    }
}

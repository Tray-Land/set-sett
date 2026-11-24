using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using SetSett.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using WinUIEx;

namespace SetSett.Services;

internal sealed class TrayIconTooltip
{
    private const int MaxLength = 127;
    private readonly TrayIcon _icon;

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, typeof(TrayIcon))]
    public TrayIconTooltip(TrayIcon icon)
    {
        _icon = icon;
        try
        {
            FieldInfo? field = typeof(TrayIcon).GetField("_windowHandle", BindingFlags.Instance | BindingFlags.NonPublic);
            WindowHandle = field?.GetValue(icon) switch
            {
                nint handle => handle,
                object boxed => ReadHandle(boxed),
                _ => 0,
            };
            if (WindowHandle == 0)
            {
                Log.Warn("Tray", $"Could not find the window for tray icon {icon.TrayIconId}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Tray", $"Could not find the window for tray icon {icon.TrayIconId}", ex);
        }
    }

    public nint WindowHandle { get; }

    // WinUIEx's icon-only update clears NIF_SHOWTIP; restore it along with the current text.
    public void Restore()
    {
        if (WindowHandle == 0)
        {
            return;
        }

        string text = _icon.Tooltip;
        NOTIFYICONDATAW data = new()
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = (HWND)WindowHandle,
            uID = _icon.TrayIconId,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP,
            szTip = text.Length > MaxLength ? text[..MaxLength] : text,
        };
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data))
        {
            Log.Warn("Tray", $"Could not restore the tooltip for tray icon {_icon.TrayIconId}");
        }
    }

    private static nint ReadHandle(object boxed)
    {
        FieldInfo? valueField = boxed.GetType().GetField("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return valueField?.GetValue(boxed) is nint handle ? handle : 0;
    }
}

using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Zinwe.Agent.Tray;

/// <summary>
/// A keyboard shortcut that works from any app (Windows' RegisterHotKey), owned by a hidden message-only window.
/// If another app already holds the combination, <see cref="IsRegistered"/> is false and nothing else changes:
/// the widget still opens from the tray icon and Start.
/// </summary>
public sealed partial class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5A49; // any id unique within this process
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private static readonly IntPtr MessageOnlyParent = new(-3); // HWND_MESSAGE

    private readonly HwndSource _source;
    private readonly Action _pressed;

    public GlobalHotkey(ModifierKeys modifiers, Key key, Action pressed)
    {
        _pressed = pressed;
        _source = new HwndSource(new HwndSourceParameters("ZinweHotkey") { ParentWindow = MessageOnlyParent, WindowStyle = 0 });
        _source.AddHook(Hook);
        IsRegistered = RegisterHotKey(_source.Handle, HotkeyId, Win32Modifiers(modifiers), (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    /// <summary>False when another app already uses the combination.</summary>
    public bool IsRegistered { get; }

    /// <summary>WPF modifiers as RegisterHotKey flags; holding the keys down fires once, not on every repeat.</summary>
    public static uint Win32Modifiers(ModifierKeys modifiers)
    {
        var flags = ModNoRepeat;
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            flags |= ModAlt;
        }
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            flags |= ModControl;
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            flags |= ModShift;
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            flags |= ModWin;
        }
        return flags;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (IsRegistered)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
        }
        _source.RemoveHook(Hook);
        _source.Dispose();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}

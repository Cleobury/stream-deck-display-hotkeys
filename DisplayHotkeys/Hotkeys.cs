using System.Runtime.InteropServices;

namespace DisplayHotkeys;

static class Hotkey
{
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    /// <summary>Parses text like "Ctrl+Alt+1" or "Win+Shift+F9".</summary>
    public static bool TryParse(string text, out uint mods, out Keys key)
    {
        mods = 0;
        key = Keys.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win" or "windows": mods |= MOD_WIN; break;
                default:
                    if (key != Keys.None) return false;
                    var name = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
                    if (char.IsDigit(name[0]) || !Enum.TryParse(name, true, out key)) return false;
                    break;
            }
        }
        return key != Keys.None;
    }

    public static string Format(Keys modifiers, bool win, Keys key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (win) parts.Add("Win");
        if (key != Keys.None)
            parts.Add(key is >= Keys.D0 and <= Keys.D9 ? ((int)(key - Keys.D0)).ToString() : key.ToString());
        return string.Join("+", parts);
    }
}

/// <summary>Runs a <see cref="HotkeyWindow"/> on its own message-loop thread.</summary>
sealed class HotkeyHost
{
    HotkeyWindow window = null!;
    SynchronizationContext ui = null!;

    public static HotkeyHost Start(Action<string> onPressed)
    {
        var host = new HotkeyHost();
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            host.ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(host.ui);
            host.window = new HotkeyWindow();
            host.window.Pressed += onPressed;
            ready.Set();
            Application.Run();
        }) { IsBackground = true, Name = "Hotkeys" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return host;
    }

    /// <summary>Replaces all registered hotkeys; returns the labels of any that another app already owns.</summary>
    public List<string> Replace(IEnumerable<(string combo, string action, string label)> hotkeys)
    {
        var failed = new List<string>();
        ui.Send(_ =>
        {
            window.UnregisterAll();
            foreach (var (combo, action, label) in hotkeys)
                if (!window.Register(combo, action)) failed.Add(label);
        }, null);
        return failed;
    }

    public void Stop() => ui.Send(_ => { window.Dispose(); Application.ExitThread(); }, null);
}

/// <summary>Hidden window that receives system-wide hotkeys (they work whatever app has focus).</summary>
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly Dictionary<int, string> actions = new();
    int nextId = 1;

    public event Action<string>? Pressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    public bool Register(string combo, string action)
    {
        if (!Hotkey.TryParse(combo, out var mods, out var key)) return false;
        int id = nextId++;
        if (!RegisterHotKey(Handle, id, mods | Hotkey.MOD_NOREPEAT, (uint)key)) return false;
        actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in actions.Keys) UnregisterHotKey(Handle, id);
        actions.Clear();
        nextId = 1;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && actions.TryGetValue((int)m.WParam, out var action))
        {
            Pressed?.Invoke(action);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}

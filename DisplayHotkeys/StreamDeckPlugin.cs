using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace DisplayHotkeys;

/// <summary>
/// Stream Deck plugin backend. Talks to the Stream Deck app over its local WebSocket and also
/// registers system-wide keyboard hotkeys, so profiles can be switched without touching the deck.
/// </summary>
sealed class StreamDeckPlugin
{
    const string SwitchAction = "com.lee-cleobury.displayhotkeys.switch";
    const string CycleAction = "com.lee-cleobury.displayhotkeys.cycle";

    record Key(string Action, string Profile, string Icon);

    static readonly string KeyImageDir = Path.Combine(AppContext.BaseDirectory, "..", "imgs", "keys");

    readonly ClientWebSocket socket = new();
    readonly Dictionary<string, string> iconShown = new();     // context → icon currently on the key
    readonly Dictionary<string, string?> imageCache = new();
    // Everything (socket events and hotkey presses) runs one at a time through this queue.
    readonly Channel<Func<Task>> work = Channel.CreateUnbounded<Func<Task>>();
    readonly Dictionary<string, Key> keys = new();
    readonly AppConfig config = ProfileStore.LoadConfig();
    HotkeyHost hotkeys = null!;
    bool hotkeysPaused;

    public async Task Run(int port, string uuid, string registerEvent)
    {
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
        await Send(new JsonObject { ["event"] = registerEvent, ["uuid"] = uuid });

        hotkeys = HotkeyHost.Start(action => work.Writer.TryWrite(() => OnHotkey(action)));
        RegisterHotkeys();

        var worker = Task.Run(async () =>
        {
            await foreach (var job in work.Reader.ReadAllAsync())
            {
                try { await job(); }
                catch (Exception ex) { await Log("Error: " + ex); }
            }
        });

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var json = JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()))?.AsObject();
            message.SetLength(0);
            if (json != null) work.Writer.TryWrite(() => Handle(json));
        }
        // Stream Deck closed the connection (app quit or plugin reloaded).
        work.Writer.TryComplete();
        hotkeys.Stop();
    }

    async Task Handle(JsonObject msg)
    {
        string ev = (string?)msg["event"] ?? "";
        string context = (string?)msg["context"] ?? "";
        string action = (string?)msg["action"] ?? "";
        var payload = msg["payload"] as JsonObject;

        switch (ev)
        {
            case "willAppear":
            case "didReceiveSettings":
                var settings = payload?["settings"];
                keys[context] = new Key(action, (string?)settings?["profile"] ?? "", (string?)settings?["icon"] ?? "monitor");
                if (ev == "willAppear") iconShown.Remove(context);
                await UpdateKey(context);
                if (ev == "didReceiveSettings") await SendState(context);
                break;
            case "willDisappear":
                keys.Remove(context);
                iconShown.Remove(context);
                break;
            case "keyDown":
                if (action == CycleAction) await Cycle(context);
                else if (keys.TryGetValue(context, out var key)) await Switch(key.Profile, context);
                break;
            case "propertyInspectorDidAppear":
                await SendState(context);
                break;
            case "propertyInspectorDidDisappear":
                ResumeHotkeys();
                break;
            case "sendToPlugin" when payload != null:
                await FromSettingsPanel(context, action, payload);
                break;
        }
    }

    async Task FromSettingsPanel(string context, string action, JsonObject payload)
    {
        keys.TryGetValue(context, out var key);
        string profile = key?.Profile ?? "";
        switch ((string?)payload["cmd"])
        {
            case "save":
                if (string.IsNullOrWhiteSpace(profile))
                {
                    await SendState(context, error: "Type a profile name first.");
                    return;
                }
                var captured = DisplayConfig.Capture(profile.Trim());
                ProfileStore.Save(captured);
                config.LastApplied = captured.Name;
                ProfileStore.SaveConfig(config);
                RegisterHotkeys();
                await RefreshKeys();
                await SendState(context, message: "Saved the current layout.");
                break;

            case "setHotkey":
                string combo = ((string?)payload["hotkey"] ?? "").Trim();
                if (combo != "" && !Hotkey.TryParse(combo, out _, out _))
                {
                    await SendState(context, error: $"'{combo}' isn't a key combination I can use.");
                    return;
                }
                if (action == CycleAction)
                {
                    RemoveCombo(combo);
                    config.CycleHotkey = combo == "" ? null : combo;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(profile))
                    {
                        await SendState(context, error: "Type a profile name first.");
                        return;
                    }
                    RemoveCombo(combo);
                    if (combo == "") config.Hotkeys.Remove(profile);
                    else config.Hotkeys[profile] = combo;
                }
                ProfileStore.SaveConfig(config);
                hotkeysPaused = false;
                var failed = RegisterHotkeys();
                await SendState(context, error: failed.Count > 0 ? $"{combo} is already used by another app. Pick a different one." : null,
                    message: failed.Count == 0 && combo != "" ? $"{combo} is ready. It works from anywhere while Stream Deck is running." : null);
                break;

            // While the panel is capturing a key combo, our own hotkeys must not swallow it.
            case "pauseHotkeys":
                hotkeysPaused = true;
                hotkeys.Replace(Array.Empty<(string, string, string)>());
                break;
            case "resumeHotkeys":
                ResumeHotkeys();
                break;

            case "refresh":
                await SendState(context);
                break;
        }
    }

    Task OnHotkey(string action) =>
        action == "cycle" ? Cycle(null) : Switch(action["apply:".Length..], null);

    async Task Cycle(string? context)
    {
        var profiles = ProfileStore.LoadAll();
        if (profiles.Count == 0)
        {
            if (context != null) await Send(Event("showAlert", context));
            return;
        }
        int i = profiles.FindIndex(p => string.Equals(p.Name, config.LastApplied, StringComparison.OrdinalIgnoreCase));
        await Switch(profiles[(i + 1) % profiles.Count].Name, context);
    }

    async Task Switch(string name, string? context)
    {
        var profile = ProfileStore.Load(name);
        if (profile == null)
        {
            await Log($"No saved profile called '{name}'.");
            if (context != null) await Send(Event("showAlert", context));
            return;
        }
        try
        {
            var warnings = await Task.Run(() => DisplayConfig.Apply(profile));
            foreach (var w in warnings) await Log(w);
            config.LastApplied = profile.Name;
            ProfileStore.SaveConfig(config);
            if (context != null) await Send(Event(warnings.Count == 0 ? "showOk" : "showAlert", context));
        }
        catch (Exception ex)
        {
            await Log($"Couldn't switch to '{name}': {ex.Message}");
            if (context != null) await Send(Event("showAlert", context));
        }
        await RefreshKeys();
    }

    List<string> RegisterHotkeys()
    {
        var names = ProfileStore.LoadAll().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = config.Hotkeys.Where(kv => names.Contains(kv.Key))
            .Select(kv => (kv.Value, "apply:" + kv.Key, $"{kv.Value} ({kv.Key})")).ToList();
        if (!string.IsNullOrEmpty(config.CycleHotkey))
            wanted.Add((config.CycleHotkey, "cycle", $"{config.CycleHotkey} (cycle)"));
        return hotkeys.Replace(wanted);
    }

    void ResumeHotkeys()
    {
        if (!hotkeysPaused) return;
        hotkeysPaused = false;
        RegisterHotkeys();
    }

    void RemoveCombo(string combo)
    {
        if (combo == "") return;
        foreach (var name in config.Hotkeys.Where(kv => string.Equals(kv.Value, combo, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            config.Hotkeys.Remove(name);
        if (string.Equals(config.CycleHotkey, combo, StringComparison.OrdinalIgnoreCase)) config.CycleHotkey = null;
    }

    async Task RefreshKeys()
    {
        foreach (var context in keys.Keys.ToList()) await UpdateKey(context);
    }

    async Task UpdateKey(string context)
    {
        if (!keys.TryGetValue(context, out var key) || key.Action != SwitchAction) return;
        bool active = key.Profile != "" && string.Equals(key.Profile, config.LastApplied, StringComparison.OrdinalIgnoreCase);
        if (!iconShown.TryGetValue(context, out var shown) || shown != key.Icon)
        {
            // State 0 = normal, state 1 = currently active profile (green).
            for (int state = 0; state < 2; state++)
                if (KeyImage(key.Icon, state == 1) is string image)
                    await Send(Event("setImage", context, new JsonObject { ["image"] = image, ["target"] = 0, ["state"] = state }));
            iconShown[context] = key.Icon;
        }
        await Send(Event("setState", context, new JsonObject { ["state"] = active ? 1 : 0 }));
        await Send(Event("setTitle", context, new JsonObject { ["title"] = key.Profile, ["target"] = 0 }));
    }

    async Task SendState(string context, string? message = null, string? error = null)
    {
        if (!keys.TryGetValue(context, out var key)) return;
        var profiles = ProfileStore.LoadAll();
        var profile = profiles.FirstOrDefault(p => string.Equals(p.Name, key.Profile, StringComparison.OrdinalIgnoreCase));
        config.Hotkeys.TryGetValue(key.Profile, out var combo);

        var state = new JsonObject
        {
            ["profiles"] = new JsonArray(profiles.Select(p => (JsonNode)p.Name).ToArray()),
            ["profile"] = key.Profile,
            ["saved"] = profile != null,
            ["savedAt"] = profile?.SavedAt.ToString("d MMM yyyy, HH:mm"),
            ["displays"] = new JsonArray((profile?.Targets ?? new()).Select(t => (JsonNode)t.Describe()).ToArray()),
            ["hotkey"] = key.Action == CycleAction ? config.CycleHotkey : combo,
            ["message"] = message,
            ["error"] = error,
        };
        await Send(new JsonObject { ["event"] = "sendToPropertyInspector", ["action"] = key.Action, ["context"] = context, ["payload"] = state });
    }

    /// <summary>Key image as a data URI, or null if there's no image by that name.</summary>
    string? KeyImage(string icon, bool active)
    {
        string file = icon + (active ? "-active" : "") + "@2x.png";
        if (imageCache.TryGetValue(file, out var cached)) return cached;
        string path = Path.Combine(KeyImageDir, file);
        string? uri = icon.All(char.IsLetterOrDigit) && File.Exists(path)
            ? "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path))
            : null;
        return imageCache[file] = uri;
    }

    static JsonObject Event(string ev, string context, JsonObject? payload = null)
    {
        var msg = new JsonObject { ["event"] = ev, ["context"] = context };
        if (payload != null) msg["payload"] = payload;
        return msg;
    }

    Task Log(string text) => Send(new JsonObject { ["event"] = "logMessage", ["payload"] = new JsonObject { ["message"] = text } });

    async Task Send(JsonObject msg)
    {
        if (socket.State != WebSocketState.Open) return;
        await socket.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}

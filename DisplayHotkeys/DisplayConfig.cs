using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DisplayHotkeys;

static class DisplayConfig
{
    const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE_PATHS = 2;
    const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_VALIDATE = 0x40, SDC_APPLY = 0x80,
        SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
    const uint INVALID_MODE_IDX = 0xFFFFFFFF;
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    static readonly int[] DpiSteps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    public static DisplayProfile Capture(string name)
    {
        var (paths, modes) = Query(QDC_ONLY_ACTIVE_PATHS);
        var profile = new DisplayProfile
        {
            Name = name,
            SavedAt = DateTime.Now,
            Paths = Convert.ToBase64String(MemoryMarshal.AsBytes(paths.AsSpan())),
            Modes = Convert.ToBase64String(MemoryMarshal.AsBytes(modes.AsSpan())),
        };

        // Adapter LUIDs change on reboot, so remember each adapter's device path and remap on apply.
        var luids = paths.SelectMany(p => new[] { p.sourceInfo.adapterId, p.targetInfo.adapterId })
            .Concat(modes.Select(m => m.adapterId)).Distinct();
        foreach (var luid in luids)
            profile.Adapters[luid.ToString()] = AdapterName(luid);

        foreach (var path in paths)
        {
            var t = new TargetSettings
            {
                Adapter = AdapterName(path.targetInfo.adapterId),
                TargetId = path.targetInfo.id,
                SourceId = path.sourceInfo.id,
                Rotation = path.targetInfo.rotation switch { 2 => 90, 3 => 180, 4 => 270, _ => 0 },
            };

            var name2 = TargetName(path.targetInfo.adapterId, path.targetInfo.id);
            t.Monitor = string.IsNullOrWhiteSpace(name2.monitorFriendlyDeviceName) ? $"Display {path.targetInfo.id}" : name2.monitorFriendlyDeviceName;
            t.MonitorDevicePath = name2.monitorDevicePath;

            if (path.sourceInfo.modeInfoIdx < modes.Length)
            {
                var sm = modes[path.sourceInfo.modeInfoIdx];
                (t.Width, t.Height, t.PositionX, t.PositionY) = ((int)sm.sourceWidth, (int)sm.sourceHeight, sm.positionX, sm.positionY);
            }
            if (path.targetInfo.refreshRate.Denominator != 0)
                t.RefreshHz = Math.Round(path.targetInfo.refreshRate.Numerator / (double)path.targetInfo.refreshRate.Denominator, 2);

            t.Hdr = GetHdr(path.targetInfo.adapterId, path.targetInfo.id);
            if (GetDpi(path.sourceInfo.adapterId, path.sourceInfo.id) is var (min, cur, _))
            {
                t.DpiScaleRel = cur;
                int idx = Math.Abs(min) + cur;
                if (idx >= 0 && idx < DpiSteps.Length) t.ScalePercent = DpiSteps[idx];
            }
            profile.Targets.Add(t);
        }
        return profile;
    }

    /// <summary>Applies a profile. Returns non-fatal warnings; throws if Windows rejects the layout.</summary>
    public static List<string> Apply(DisplayProfile profile, bool validateOnly = false)
    {
        var warnings = new List<string>();
        var paths = MemoryMarshal.Cast<byte, DISPLAYCONFIG_PATH_INFO>(Convert.FromBase64String(profile.Paths)).ToArray();
        var modes = MemoryMarshal.Cast<byte, DISPLAYCONFIG_MODE_INFO>(Convert.FromBase64String(profile.Modes)).ToArray();
        var current = CurrentAdapters();

        LUID Remap(LUID saved)
        {
            if (!profile.Adapters.TryGetValue(saved.ToString(), out var adapterName))
                return saved;
            if (!current.TryGetValue(adapterName, out var now))
                throw new InvalidOperationException("A graphics adapter used by this profile wasn't found. Re-save the profile.");
            return now;
        }
        for (int i = 0; i < paths.Length; i++)
        {
            paths[i].sourceInfo.adapterId = Remap(paths[i].sourceInfo.adapterId);
            paths[i].targetInfo.adapterId = Remap(paths[i].targetInfo.adapterId);
        }
        for (int i = 0; i < modes.Length; i++)
            modes[i].adapterId = Remap(modes[i].adapterId);

        uint flags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES |
            (validateOnly ? SDC_VALIDATE : SDC_APPLY | SDC_SAVE_TO_DATABASE);
        int result = Native.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
        if (validateOnly)
        {
            if (result != 0) throw new Win32Exception(result, $"Windows rejected the layout (error {result}).");
            return warnings;
        }

        if (result != 0)
        {
            // Turn the right displays on first and let Windows pick modes, then retry the exact modes.
            var bare = (DISPLAYCONFIG_PATH_INFO[])paths.Clone();
            for (int i = 0; i < bare.Length; i++)
            {
                bare[i].sourceInfo.modeInfoIdx = INVALID_MODE_IDX;
                bare[i].targetInfo.modeInfoIdx = INVALID_MODE_IDX;
            }
            int fallback = Native.SetDisplayConfig((uint)bare.Length, bare, 0, null, flags);
            if (fallback != 0)
                throw new Win32Exception(result, $"Windows rejected the layout (error {result}). Are all of this profile's displays connected and switched on?");
            Thread.Sleep(1000);
            if (Native.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags) != 0)
                warnings.Add("Displays switched, but the exact resolution/position couldn't be restored.");
        }

        // HDR and scaling can only be set once the targets are active.
        Thread.Sleep(750);
        foreach (var t in profile.Targets)
        {
            if (!current.TryGetValue(t.Adapter, out var luid)) continue;

            if (t.Hdr is bool wantHdr && GetHdr(luid, t.TargetId) is bool hasHdr && hasHdr != wantHdr && !SetHdr(luid, t.TargetId, wantHdr))
                warnings.Add($"Couldn't turn HDR {(wantHdr ? "on" : "off")} for {t.Monitor}.");

            if (t.DpiScaleRel is int wantRel && GetDpi(luid, t.SourceId) is var (min, cur, max) && cur != wantRel)
            {
                var dpi = new DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
                {
                    header = Native.Header<DISPLAYCONFIG_SOURCE_DPI_SCALE_SET>(Native.SET_DPI_SCALE, luid, t.SourceId),
                    scaleRel = Math.Clamp(wantRel, min, max),
                };
                if (Native.SetInfo(ref dpi) != 0) warnings.Add($"Couldn't restore the scale setting for {t.Monitor}.");
            }
        }
        return warnings;
    }

    static (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) Query(uint flags)
    {
        for (int attempt = 0; ; attempt++)
        {
            int r = Native.GetDisplayConfigBufferSizes(flags, out uint numPaths, out uint numModes);
            if (r != 0) throw new Win32Exception(r);
            var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
            var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
            r = Native.QueryDisplayConfig(flags, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (r == ERROR_INSUFFICIENT_BUFFER && attempt < 5) continue;
            if (r != 0) throw new Win32Exception(r);
            return (paths[..(int)numPaths], modes[..(int)numModes]);
        }
    }

    static Dictionary<string, LUID> CurrentAdapters()
    {
        var (paths, modes) = Query(QDC_ALL_PATHS);
        var map = new Dictionary<string, LUID>(StringComparer.OrdinalIgnoreCase);
        foreach (var luid in paths.Select(p => p.targetInfo.adapterId).Concat(modes.Select(m => m.adapterId)).Distinct())
            map.TryAdd(AdapterName(luid), luid);
        return map;
    }

    static string AdapterName(LUID luid)
    {
        var info = new DISPLAYCONFIG_ADAPTER_NAME { header = Native.Header<DISPLAYCONFIG_ADAPTER_NAME>(Native.GET_ADAPTER_NAME, luid, 0) };
        return Native.GetInfo(ref info) == 0 ? info.adapterDevicePath : luid.ToString();
    }

    static DISPLAYCONFIG_TARGET_DEVICE_NAME TargetName(LUID adapter, uint targetId)
    {
        var info = new DISPLAYCONFIG_TARGET_DEVICE_NAME { header = Native.Header<DISPLAYCONFIG_TARGET_DEVICE_NAME>(Native.GET_TARGET_NAME, adapter, targetId) };
        Native.GetInfo(ref info);
        return info;
    }

    /// <summary>HDR toggle state, or null if the display doesn't support HDR.</summary>
    static bool? GetHdr(LUID adapter, uint targetId)
    {
        var v2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 { header = Native.Header<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(Native.GET_ADVANCED_COLOR_INFO_2, adapter, targetId) };
        if (Native.GetInfo(ref v2) == 0)
            return (v2.value & 0x10) != 0 ? (v2.value & 0x20) != 0 : null; // highDynamicRangeSupported / UserEnabled

        var v1 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO { header = Native.Header<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(Native.GET_ADVANCED_COLOR_INFO, adapter, targetId) };
        if (Native.GetInfo(ref v1) == 0 && (v1.value & 1) != 0) // advancedColorSupported
            return (v1.value & 2) != 0; // advancedColorEnabled
        return null;
    }

    static bool SetHdr(LUID adapter, uint targetId, bool enable)
    {
        var hdr = new DISPLAYCONFIG_SET_STATE { header = Native.Header<DISPLAYCONFIG_SET_STATE>(Native.SET_HDR_STATE, adapter, targetId), value = enable ? 1u : 0u };
        if (Native.SetInfo(ref hdr) == 0) return true;
        var legacy = new DISPLAYCONFIG_SET_STATE { header = Native.Header<DISPLAYCONFIG_SET_STATE>(Native.SET_ADVANCED_COLOR_STATE, adapter, targetId), value = enable ? 1u : 0u };
        return Native.SetInfo(ref legacy) == 0;
    }

    static (int min, int cur, int max)? GetDpi(LUID adapter, uint sourceId)
    {
        var info = new DISPLAYCONFIG_SOURCE_DPI_SCALE_GET { header = Native.Header<DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>(Native.GET_DPI_SCALE, adapter, sourceId) };
        return Native.GetInfo(ref info) == 0 ? (info.minScaleRel, info.curScaleRel, info.maxScaleRel) : null;
    }
}

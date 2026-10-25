using System.Runtime.InteropServices;

namespace DisplayHotkeys;

// Windows CCD (Connecting and Configuring Displays) API — the same API the
// Windows Settings > Display page uses to switch monitors, modes and HDR.

[StructLayout(LayoutKind.Sequential)]
public struct LUID
{
    public uint LowPart;
    public int HighPart;
    public override string ToString() => $"{HighPart:X8}{LowPart:X8}";
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint outputTechnology;
    public uint rotation;
    public uint scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public uint scanLineOrdering;
    public int targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

// The native struct has a union after adapterId; only the fields we read are mapped.
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DISPLAYCONFIG_MODE_INFO
{
    public const uint SOURCE = 1, TARGET = 2;

    [FieldOffset(0)] public uint infoType;
    [FieldOffset(4)] public uint id;
    [FieldOffset(8)] public LUID adapterId;

    // infoType == SOURCE (DISPLAYCONFIG_SOURCE_MODE)
    [FieldOffset(16)] public uint sourceWidth;
    [FieldOffset(20)] public uint sourceHeight;
    [FieldOffset(28)] public int positionX;
    [FieldOffset(32)] public int positionY;

    // infoType == TARGET (DISPLAYCONFIG_VIDEO_SIGNAL_INFO)
    [FieldOffset(32)] public uint vSyncNumerator;
    [FieldOffset(36)] public uint vSyncDenominator;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public int type;
    public uint size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DISPLAYCONFIG_ADAPTER_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string adapterDevicePath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public uint outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint value;
    public uint colorEncoding;
    public uint bitsPerColorChannel;
}

// Windows 11 24H2+: separates "HDR on" from Auto Color Management.
[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint value;
    public uint colorEncoding;
    public uint bitsPerColorChannel;
    public uint activeColorMode;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SET_STATE
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint value;
}

// Undocumented, but what Settings uses for the per-monitor "Scale" dropdown.
[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public int minScaleRel;
    public int curScaleRel;
    public int maxScaleRel;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public int scaleRel;
}

static class Native
{
    public const int GET_TARGET_NAME = 2, GET_ADAPTER_NAME = 4,
        GET_ADVANCED_COLOR_INFO = 9, SET_ADVANCED_COLOR_STATE = 10,
        GET_ADVANCED_COLOR_INFO_2 = 15, SET_HDR_STATE = 16,
        GET_DPI_SCALE = -3, SET_DPI_SCALE = -4;

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(uint numPaths, DISPLAYCONFIG_PATH_INFO[]? paths,
        uint numModes, DISPLAYCONFIG_MODE_INFO[]? modes, uint flags);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    static extern int GetDeviceInfo(IntPtr packet);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    static extern int SetDeviceInfo(IntPtr packet);

    public static DISPLAYCONFIG_DEVICE_INFO_HEADER Header<T>(int type, LUID adapter, uint id) where T : struct =>
        new() { type = type, size = (uint)Marshal.SizeOf<T>(), adapterId = adapter, id = id };

    public static int GetInfo<T>(ref T packet) where T : struct => Call(ref packet, GetDeviceInfo);
    public static int SetInfo<T>(ref T packet) where T : struct => Call(ref packet, SetDeviceInfo);

    static int Call<T>(ref T packet, Func<IntPtr, int> fn) where T : struct
    {
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(packet, p, false);
            int result = fn(p);
            packet = Marshal.PtrToStructure<T>(p);
            return result;
        }
        finally { Marshal.FreeHGlobal(p); }
    }
}

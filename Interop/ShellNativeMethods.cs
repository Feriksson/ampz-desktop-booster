using System.Runtime.InteropServices;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// P/Invoke para la AppBar (la barra) y el monitoreo de sistema (CPU/RAM/batería/red).
/// Convive con el resto de <see cref="NativeMethods"/> (el hook de teclado) vía partial class:
/// son la misma "puerta" a Win32, separada en archivos por responsabilidad.
/// </summary>
internal static partial class NativeMethods
{
    // ---- AppBar (shell32.dll) ----

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    public static extern int RegisterWindowMessage(string msg);

    // ---- Pantalla / DPI ----

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    // ---- CPU (kernel32.dll) ----

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    // ---- RAM (kernel32.dll) ----

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---- Batería (kernel32.dll) ----

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    // ---- Constantes AppBar ----

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    public const uint ABM_NEW = 0x00000000;
    public const uint ABM_REMOVE = 0x00000001;
    public const uint ABM_QUERYPOS = 0x00000002;
    public const uint ABM_SETPOS = 0x00000003;
    public const uint ABM_GETSTATE = 0x00000004;

    public const uint ABN_STATECHANGE = 0x0000000;
    public const uint ABN_POSCHANGED = 0x0000001;
    public const uint ABN_FULLSCREENAPP = 0x0000002;

    public const int ABE_LEFT = 0;
    public const int ABE_TOP = 1;
    public const int ABE_RIGHT = 2;
    public const int ABE_BOTTOM = 3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct APPBARDATA
{
    public int cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public int uEdge;
    public RECT rc;
    public IntPtr lParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILETIME
{
    public uint dwLowDateTime;
    public uint dwHighDateTime;

    public readonly ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_POWER_STATUS
{
    public byte ACLineStatus;        // 0 = batería, 1 = enchufado, 255 = desconocido
    public byte BatteryFlag;
    public byte BatteryLifePercent;  // 0-100, 255 = desconocido
    public byte SystemStatusFlag;
    public int BatteryLifeTime;
    public int BatteryFullLifeTime;
}

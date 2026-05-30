using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// Lee y cambia la frecuencia de refresco del monitor primario (Win+F12 del legacy).
/// En vez de hardcodear 60/240, ENUMERAMOS los modos reales del display y ofrecemos sólo
/// las frecuencias válidas para la resolución actual. P/Invoke clásico (DllImport): el DEVMODE
/// con campos string no se lleva bien con el source-gen de LibraryImport.
/// </summary>
public static class DisplaySettings
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DM_DISPLAYFREQUENCY = 0x00400000;
    private const int CDS_UPDATEREGISTRY = 0x01;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    /// <summary>Frecuencia actual del monitor primario (Hz). 0 si no se pudo leer.</summary>
    public static int CurrentRate()
    {
        var dm = NewDevmode();
        return EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm)
            ? (int)dm.dmDisplayFrequency
            : 0;
    }

    /// <summary>Frecuencias disponibles para la resolución actual, ordenadas, sin duplicados.</summary>
    public static IReadOnlyList<int> AvailableRates()
    {
        var cur = NewDevmode();
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref cur))
            return Array.Empty<int>();

        var rates = new SortedSet<int>();
        var dm = NewDevmode();
        for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++)
        {
            // Sólo modos de la resolución actual — no tiene sentido ofrecer un Hz de otra res.
            if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight && dm.dmDisplayFrequency > 1)
                rates.Add((int)dm.dmDisplayFrequency);
            dm = NewDevmode();
        }
        return rates.ToList();
    }

    /// <summary>Cambia la frecuencia del monitor primario. true si lo aplicó.</summary>
    public static bool SetRate(int hz)
    {
        var dm = NewDevmode();
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
            return false;
        dm.dmFields = DM_DISPLAYFREQUENCY;
        dm.dmDisplayFrequency = (uint)hz;
        return ChangeDisplaySettings(ref dm, CDS_UPDATEREGISTRY) == DISP_CHANGE_SUCCESSFUL;
    }

    private static DEVMODE NewDevmode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettings(ref DEVMODE lpDevMode, int dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}

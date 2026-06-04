using System.Runtime.InteropServices;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// Helpers de MONITOR (pantalla física) para el ventaneo del Paths Manager. Reusa el interop de
/// monitor que ya vive en el partial Fullscreen (<c>MonitorFromWindow</c>, <c>GetMonitorInfo</c>,
/// <c>GetWindowRect</c>, <c>MONITORINFO</c>, <c>MONITOR_DEFAULTTONEAREST</c>) — son privados pero los
/// vemos por ser la MISMA <c>partial class</c>.
///
/// Por qué existe: un escritorio virtual de Windows abarca TODOS los monitores, así que filtrar por
/// desk NO distingue pantallas. Cuando reabrimos una carpeta (default del Paths Manager) queremos que
/// aparezca EN EL MONITOR donde está el usuario, sin catapultarlo a otra pantalla donde la carpeta
/// quedó abierta antes. Para eso necesitamos saber en qué monitor está cada ventana y, si hace falta,
/// mover la nueva al monitor objetivo.
/// </summary>
internal static partial class WindowMethods
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight,
        [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    /// <summary>true si la ventana está MAXIMIZADA (a MoveWindow no le gusta reubicar una maximizada).</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    /// <summary>HMONITOR donde vive la ventana (el más cercano). <c>Zero</c> si la ventana es nula.</summary>
    public static IntPtr MonitorOf(IntPtr hwnd) =>
        hwnd == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

    /// <summary>
    /// Lleva la ventana al <paramref name="hMonitor"/> objetivo y la centra en su ÁREA DE TRABAJO
    /// (rcWork, lo que deja libre la barra). No-op si ya está en ese monitor (no la movemos al pedo).
    /// Si vino maximizada, la restauramos primero — MoveWindow no reubica una ventana maximizada; tras
    /// moverla queda en tamaño normal en la pantalla correcta, que es justo lo que queremos al traer
    /// una carpeta "a donde estoy".
    /// </summary>
    public static void MoveToMonitor(IntPtr hwnd, IntPtr hMonitor)
    {
        if (hwnd == IntPtr.Zero || hMonitor == IntPtr.Zero) return;
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == hMonitor) return; // ya está ahí

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref mi)) return;

        if (IsZoomed(hwnd)) ShowWindow(hwnd, SW_RESTORE); // re-leer el rect DESPUÉS del restore
        if (!GetWindowRect(hwnd, out RECT r)) return;

        int w = r.right - r.left;
        int h = r.bottom - r.top;
        RECT work = mi.rcWork;
        int areaW = work.right - work.left;
        int areaH = work.bottom - work.top;

        // Centrada; si la ventana es más grande que el área, la anclamos arriba-izquierda (Max con 0).
        int x = work.left + System.Math.Max(0, (areaW - w) / 2);
        int y = work.top  + System.Math.Max(0, (areaH - h) / 2);
        MoveWindow(hwnd, x, y, w, h, bRepaint: true);
    }
}

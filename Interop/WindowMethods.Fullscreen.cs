using System.Runtime.InteropServices;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// Detección de "hay una app en PANTALLA COMPLETA en el monitor de la barra". Cubre el caso que
/// ABN_FULLSCREENAPP NO cubre: el fullscreen "borderless windowed" (YouTube con F, juegos modernos,
/// video maximizado en el navegador). Para Windows eso es sólo una ventana SIN BORDES tapando la
/// pantalla — no un cambio de modo de display — así que no manda la notificación de appbar. Acá lo
/// deducimos por GEOMETRÍA: una ventana es fullscreen cuando su rect cubre el MONITOR FÍSICO COMPLETO
/// (rcMonitor). Una ventana MAXIMIZADA normal, en cambio, sólo llega al área de trabajo (rcWork) y por
/// eso NO tapa la barra — esa es justo la diferencia que usamos para no bajar la barra de más.
/// </summary>
internal static partial class WindowMethods
{
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint MONITORINFOF_PRIMARY     = 0x00000001;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;        // hay que setearlo ANTES de GetMonitorInfo o falla
        public RECT rcMonitor;    // rect FÍSICO completo del monitor (incluye taskbar/appbars)
        public RECT rcWork;       // área de trabajo (lo que deja libre la barra) — no se usa acá
        public uint dwFlags;      // MONITORINFOF_PRIMARY si es el monitor primario
    }

    /// <summary>
    /// true si la ventana en PRIMER PLANO está en pantalla completa SOBRE EL MONITOR PRIMARIO (donde
    /// vive la barra). Excluimos el escritorio/shell (Progman/WorkerW cubren el monitor por
    /// definición, no son una app) y, por las dudas, la propia barra (<paramref name="barHwnd"/>).
    /// Multi-monitor: si el fullscreen está en un monitor secundario, la barra del primario no
    /// estorba → devolvemos false para no bajarla al pedo.
    /// </summary>
    public static bool IsForegroundFullscreenOnPrimary(IntPtr barHwnd)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == barHwnd) return false;
        if (fg == GetShellWindow() || fg == GetDesktopWindow()) return false;

        // Clases del shell que SIEMPRE cubren el monitor pero NO son una app en fullscreen.
        string cls = ClassOf(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        if (!GetWindowRect(fg, out RECT w)) return false;

        IntPtr mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;

        // Sólo nos importa el fullscreen en el monitor PRIMARIO: ahí está clavada la barra.
        if ((mi.dwFlags & MONITORINFOF_PRIMARY) == 0) return false;

        RECT m = mi.rcMonitor;
        // La ventana cubre TODO el monitor físico → fullscreen. Una maximizada normal sólo llega a
        // rcWork (deja la franja de la barra), así que no entra acá. Uso <=/>= por si hay overscan.
        return w.left <= m.left && w.top <= m.top && w.right >= m.right && w.bottom >= m.bottom;
    }
}

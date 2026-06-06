using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// Helpers Win32 para el gobierno de ventanas (pins + restricciones): nombre de proceso desde
/// un hwnd, detección de "ventana top-level real", y maximizar. P/Invoke clásico (DllImport):
/// estos no necesitan source-gen y conviven con la parte LibraryImport de WindowMethods.
/// </summary>
internal static partial class WindowMethods
{
    public const int GWL_STYLE = -16;
    public const long WS_CHILD = 0x40000000;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_MINIMIZE = 6;
    private const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    /// <summary>
    /// HWNDs de TODAS las ventanas top-level "reales" (visibles, no child, no tool, etc.) que
    /// pertenecen a un proceso con el nombre dado (ej. "Code.exe"). Incluye ventanas de otros
    /// virtual desktops (no filtramos cloaked). Lo usa el watchdog post-spawn para detectar la
    /// ventana NUEVA recién creada y aplicarle un estado (ej. maximizar).
    /// </summary>
    public static List<IntPtr> VisibleTopLevelOf(string exeName)
    {
        var list = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsRealTopLevel(hwnd)) return true;
            if (string.Equals(ProcessNameOf(hwnd), exeName, StringComparison.OrdinalIgnoreCase))
                list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Nombre del proceso dueño del hwnd (ej. "brave.exe"), o "" si no se pudo.</summary>
    public static string ProcessNameOf(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return "";
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch { return ""; }
    }

    /// <summary>
    /// true si es una ventana top-level "real" — la que el usuario ve como app: visible, sin
    /// padre, sin owner, no child, no tool window. Filtra diálogos hijos, popups, etc.
    /// </summary>
    public static bool IsRealTopLevel(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (!IsWindowVisible(hWnd)) return false;
        if (GetParent(hWnd) != IntPtr.Zero) return false;
        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return false;
        long style = GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();
        if ((style & WS_CHILD) != 0) return false;
        long ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        return true;
    }

    /// <summary>Maximiza la ventana. ShowWindow(3) funciona aun en ventanas de otro desktop.</summary>
    public static void Maximize(IntPtr hWnd) => ShowWindow(hWnd, SW_SHOWMAXIMIZED);

    /// <summary>
    /// Minimiza TODAS las ventanas top-level reales de OTRAS apps en el desk ACTUAL — nuestro
    /// "Mostrar escritorio", pero salteando las NUESTRAS (la barra, el overlay) para que la barra
    /// NO se esconda. Reemplaza al Win+D nativo, que tapa la barra peleando el z-order — batalla
    /// imposible para una AppBar de terceros (sólo la taskbar del shell está exenta). El hook de
    /// teclado intercepta Win+D y llama acá en su lugar.
    ///
    /// Sólo toca el desk actual: las ventanas de otros escritorios virtuales figuran "cloaked" y se
    /// saltean. Junta primero y minimiza después (no mutar mientras EnumWindows itera). Excluimos por
    /// CLASE al escritorio (Progman/WorkerW) y a la taskbar (Shell_TrayWnd), que pasan IsRealTopLevel.
    /// </summary>
    public static void MinimizeForeignTopLevel(string ownProcess)
    {
        var targets = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsRealTopLevel(hwnd) || IsCloaked(hwnd)) return true; // no real / otro desk / fantasma
            if (string.Equals(ProcessNameOf(hwnd), ownProcess, StringComparison.OrdinalIgnoreCase))
                return true;                                            // NO minimizar lo nuestro (la barra)
            string cls = ClassOf(hwnd);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return true;                                            // escritorio / taskbar del shell
            targets.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        foreach (var h in targets)
            ShowWindow(h, SW_MINIMIZE);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const int DWMWA_CLOAKED = 14;

    /// <summary>
    /// true si DWM tiene "cloaked" (oculta) la ventana. Pasa con apps UWP suspendidas y con los
    /// hosts invisibles del shell (TextInputHost = "Experiencia de entrada de Windows", etc.) — esos
    /// reportan IsWindowVisible=true pero no son ventanas reales en pantalla.
    ///
    /// OJO: una ventana en OTRO escritorio virtual TAMBIÉN figura cloaked. Por eso esto NO se mete en
    /// <see cref="IsRealTopLevel"/> (rompería el mover ventanas entre desks). Sólo es seguro llamarlo
    /// sobre ventanas YA confirmadas en el desk ACTUAL, donde "cloaked" únicamente puede significar
    /// "fantasma del shell", no "está en otro escritorio".
    /// </summary>
    public static bool IsCloaked(IntPtr hWnd) =>
        DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>
    /// Apps con una ventana top-level real y VISIBLE ahora mismo — para pickers (ej. agregar a una
    /// whitelist desde la config). Devuelve proceso → título representativo, deduplicado por proceso
    /// y ordenado por título. Salta fantasmas del shell (cloaked) y ventanas sin título útil.
    /// </summary>
    public static List<(string Proc, string Title)> RunningTopLevelApps()
    {
        var byProc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((hwnd, _) =>
        {
            if (!IsRealTopLevel(hwnd) || IsCloaked(hwnd)) return true;
            string proc = ProcessNameOf(hwnd);
            if (proc == "") return true;
            string title = TextOf(hwnd);
            if (title == "" || title == "Program Manager") return true;
            byProc.TryAdd(proc, title); // primer título visto por proceso
            return true;
        }, IntPtr.Zero);

        return byProc
            .Select(kv => (Proc: kv.Key, Title: kv.Value))
            .OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

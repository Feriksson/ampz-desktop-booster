using System;
using System.Diagnostics;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Apps;

/// <summary>
/// Acciones sin ventana propia: abrir Descargas (desktop-aware) y abrir Windows Terminal en la
/// carpeta del Explorer activo. Las dispara el router directo desde un hotkey.
/// </summary>
public static class QuickActions
{
    private static readonly string[] DownloadTitles = { "Descargas", "Downloads" };

    /// <summary>
    /// Win+F11: si ya hay una ventana de Descargas en ESTE virtual desktop, la activa; si no,
    /// abre una nueva. Evita duplicar ventanas de Descargas por desktop (como el legacy).
    /// </summary>
    public static void OpenDownloads(DesktopService desktops)
    {
        int current = desktops.Current;
        IntPtr found = IntPtr.Zero;

        WindowMethods.EnumWindows((hwnd, _) =>
        {
            if (!WindowMethods.IsWindowVisible(hwnd)) return true;
            if (WindowMethods.ClassOf(hwnd) != "CabinetWClass") return true;

            string title = WindowMethods.TextOf(hwnd);
            bool isDownloads = false;
            foreach (var t in DownloadTitles)
                if (title.Equals(t, StringComparison.OrdinalIgnoreCase)) { isDownloads = true; break; }
            if (!isDownloads) return true;

            if (VirtualDesktopAccessor.GetWindowDesktopNumber(hwnd) == current)
            {
                found = hwnd;
                return false; // cortar la enumeración
            }
            return true;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero)
        {
            WindowMethods.ShowWindow(found, WindowMethods.SW_RESTORE);
            WindowMethods.SetForegroundWindow(found);
            return;
        }

        // No había en este desk → abrir nueva (explorer la lanza en el desktop actual).
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "shell:Downloads", UseShellExecute = true }); }
        catch { }
    }

    /// <summary>Win+`: abre el shell preferido (pwsh → powershell) parado en cada carpeta target.</summary>
    public static void OpenTerminalInExplorerPath()
    {
        foreach (var path in ExplorerContext.GetTargetPaths())
        {
            try { Shell.OpenInDir(path); }
            catch { }
        }
    }
}

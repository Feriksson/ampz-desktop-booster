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

        IntPtr found = WindowMethods.FindVisible(hwnd => IsDownloadsOn(hwnd, current));
        if (found != IntPtr.Zero)
        {
            // Ya hay una en este desk → traerla al frente con el mismo ForceForeground confiable que
            // el resto (preserveMaximized: si está maximizada no la achicamos, sólo des-minimizamos).
            // Antes era Show(SW_RESTORE)+SetForegroundWindow a secas, que no vence el anti-robo de
            // foco cuando venimos de un hotkey global y encima des-maximizaba.
            WindowMethods.ForceForeground(found);
            return;
        }

        // No había en este desk → abrir nueva (explorer la lanza en el desktop actual) y, como la
        // crea explorer.exe (no nosotros), forzarle el foreground cuando aparezca.
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "shell:Downloads", UseShellExecute = true }); }
        catch { return; }
        WindowFocuser.FocusWhenReady(hwnd => IsDownloadsOn(hwnd, current));
    }

    /// <summary>¿Es <paramref name="hwnd"/> la ventana de Descargas en el escritorio virtual dado?</summary>
    private static bool IsDownloadsOn(IntPtr hwnd, int desktop)
    {
        if (WindowMethods.ClassOf(hwnd) != "CabinetWClass") return false;
        string title = WindowMethods.TextOf(hwnd);
        bool isDownloads = false;
        foreach (var t in DownloadTitles)
            if (title.Equals(t, StringComparison.OrdinalIgnoreCase)) { isDownloads = true; break; }
        return isDownloads && VirtualDesktopAccessor.GetWindowDesktopNumber(hwnd) == desktop;
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

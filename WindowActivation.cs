using System.Windows;
using System.Windows.Interop;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster;

/// <summary>
/// Extensiones para mostrar ventanas utilitarias con foco de teclado CONFIABLE. Las utilidades se
/// abren desde hotkeys globales: en ese instante nuestro proceso NO es el foreground, así que
/// <c>Show()</c> + <c>Activate()</c> no alcanza (Windows bloquea el cambio de foreground como
/// protección anti-robo-de-foco). Forzamos el primer plano con el truco de AttachThreadInput
/// (ver <see cref="WindowMethods.ForceForeground"/>).
/// </summary>
internal static class WindowActivation
{
    /// <summary>Muestra la ventana y le fuerza el primer plano + foco de teclado.</summary>
    public static void ShowFocused(this Window window)
    {
        window.Show();
        // Show() es síncrono: al volver, el HWND ya existe y la ventana está visible.
        var hwnd = new WindowInteropHelper(window).Handle;
        WindowMethods.ForceForeground(hwnd);
    }

    /// <summary>Trae al frente una ventana YA abierta (re-press de singletons: Config/Notes/Paths).</summary>
    public static void BringToFront(this Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        WindowMethods.ForceForeground(hwnd);
    }
}

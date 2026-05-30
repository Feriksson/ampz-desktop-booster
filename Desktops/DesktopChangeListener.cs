using System;
using System.Windows.Interop;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Escucha CUALQUIER cambio de desktop virtual — por hotkey nuestro, por Win+Ctrl+Flechas,
/// por la taskbar, lo que sea. La DLL postea un mensaje a una ventana mensajera oculta cada
/// vez que cambia el desktop; nosotros lo traducimos al evento <see cref="DesktopChanged"/>
/// con el índice del nuevo desktop.
///
/// Es el equivalente exacto del RegisterPostMessageHook + OnMessage(0x5100) del legacy:
/// una sola fuente de verdad que alimenta el overlay central Y el widget de la barra.
/// </summary>
public sealed class DesktopChangeListener : IDisposable
{
    // Mismo offset que usaba el .ahk. El id del mensaje posteado == este offset.
    private const int WM_VD_CHANGED = 0x5100;

    private readonly HwndSource _source;

    /// <summary>Se dispara con el índice del desktop al que se acaba de cambiar.</summary>
    public event Action<int>? DesktopChanged;

    public DesktopChangeListener()
    {
        // Ventana MESSAGE-ONLY (parent = HWND_MESSAGE): recibe el PostMessage de la DLL pero
        // NUNCA aparece en la taskbar ni en el Alt-Tab. Una HwndSource top-level común sí
        // figura como "una app más" — ése era el programa fantasma que se veía en la barra.
        var p = new HwndSourceParameters("AmpzDesktopBooster_VDListener")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);

        VirtualDesktopAccessor.RegisterPostMessageHook(_source.Handle, WM_VD_CHANGED);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_VD_CHANGED)
        {
            DesktopChanged?.Invoke(lParam.ToInt32()); // lParam = índice del nuevo desktop
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => _source.Dispose();
}

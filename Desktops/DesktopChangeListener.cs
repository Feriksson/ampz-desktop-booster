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
///
/// ⚠ El aviso del shell LLEGA SIEMPRE pero a veces TARDE — y por eso acá ya NO hay polling.
/// Durante dos semanas hubo un poll de 250ms "de red" que anotaba cada cambio que detectaba antes
/// que el aviso como "aviso perdido" (262 entradas en vd-listener.log). Medido el 2026-10-10 con
/// una línea por evento: de 27 cambios el aviso llegó en los 27; en 4 el poll lo vio primero y el
/// aviso apareció 44–167ms DESPUÉS, para el mismo desk. No se perdía nada: el poll le ganaba la
/// carrera a un aviso que venía en camino. (La pérdida REAL de avisos que motivó el poll era el
/// double free de la DLL al renombrar, curado con la build parcheada — ver el CLAUDE.md.)
///
/// Lo que sí se sentía era esa demora del shell: el popup aparecía tarde. La cura no es sondear
/// más rápido sino no esperar al shell cuando no hace falta: los cambios que hace la PROPIA app
/// (Win+Numpad, DeskPicker, launcher) entran por <see cref="NotifyLocalChange"/> en el acto — la
/// app ya sabe adónde fue. El aviso del shell queda para lo que NO pasa por nosotros
/// (Win+Ctrl+Flechas, vista de tareas, gestos), y cuando llega tarde para un cambio ya anunciado
/// se descarta contra el desk REAL (ver WndProc).
///
/// NO se re-suscribe en caliente (Unregister + RestartVirtualDesktopAccessor + Register crasheó
/// bajo cdb: use-after-free en la DLL). Dispose SE DESUSCRIBE.
/// </summary>
public sealed class DesktopChangeListener : IDisposable
{
    // Mismo offset que usaba el .ahk. El id del mensaje posteado == este offset.
    private const int WM_VD_CHANGED = 0x5100;

    private readonly HwndSource _source;
    private int _lastIndex; // último desk ANUNCIADO: dedupe entre el aviso local y el del shell

    /// <summary>Último desk anunciado. Sirve para descartar trabajo diferido que quedó viejo.</summary>
    public int LastIndex => _lastIndex;

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

        _lastIndex = VirtualDesktopAccessor.GetCurrentDesktopNumber();
    }

    /// <summary>
    /// Un cambio de desk que hizo la PROPIA app: se anuncia ya, sin esperar el aviso del shell (que
    /// puede tardar más de 150ms). Idempotente: si ese desk ya está anunciado no hace nada.
    /// </summary>
    public void NotifyLocalChange(int index)
    {
        if (index < 0 || index == _lastIndex) return;
        Raise(index);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_VD_CHANGED)
        {
            // La verdad es el desk REAL de ahora, no el lParam: el aviso puede llegar tarde, y en
            // una ráfaga A→B→A anunciada en local el aviso atrasado de B llegaría cuando ya estamos
            // en A — obedecerlo pintaría B un instante (popup y barra con el desk equivocado).
            int current;
            try { current = VirtualDesktopAccessor.GetCurrentDesktopNumber(); }
            catch { current = lParam.ToInt32(); } // DLL en un estado raro: nos quedamos con el aviso
            if (current >= 0 && current != _lastIndex)
                Raise(current);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Raise(int index)
    {
        _lastIndex = index;
        DesktopChanged?.Invoke(index);
    }

    public void Dispose()
    {
        try { VirtualDesktopAccessor.UnregisterPostMessageHook(_source.Handle); } catch { }
        _source.Dispose();
    }
}

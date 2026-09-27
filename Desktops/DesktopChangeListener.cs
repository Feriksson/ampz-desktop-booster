using System;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Persistence;

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
/// ⚠ El aviso de la DLL NO es confiable por sí solo — de ahí la RED de polling (no la saques).
/// Caso real: tras un cierre abrupto de la app (crash nativo), al relanzarla la navegación
/// andaba (Win+Numpad2 llevaba a NOTES) pero el mensaje 0x5100 no llegaba NUNCA más: la barra y
/// el overlay quedaban congelados en el desk del arranque, y sólo un reinicio de Windows lo
/// arreglaba. Verificado: desde otro proceso la DLL reportaba bien el desk actual — lo roto era
/// la entrega de notificaciones (vienen del shell, explorer), no la consulta.
/// Por eso: (1) un poll barato del desk actual cada 250ms dispara el MISMO evento si el aviso no
/// llegó — la UI nunca se congela, esté como esté el shell; (2) al detectar un aviso perdido se
/// intenta RE-SUSCRIBIR (Unregister + RestartVirtualDesktopAccessor + Register), con log a
/// archivo para saber si eso cura el problema de raíz; (3) Dispose SE DESUSCRIBE (antes no lo
/// hacía: incluso saliendo bien dejábamos la suscripción colgada en el shell).
/// </summary>
public sealed class DesktopChangeListener : IDisposable
{
    // Mismo offset que usaba el .ahk. El id del mensaje posteado == este offset.
    private const int WM_VD_CHANGED = 0x5100;

    // 250ms: imperceptible para el ojo al cambiar de desk y despreciable en costo (una consulta al
    // shell). El debounce del overlay (40ms) coalesce un eventual doble disparo aviso+poll.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // No reintentar la re-suscripción en ráfaga: si el shell no la acepta, con el poll la UI ya
    // anda — insistir cada 250ms sólo haría ruido.
    private static readonly TimeSpan ResubscribeCooldown = TimeSpan.FromSeconds(30);

    private static string LogPath => Path.Combine(AppPaths.DataDir, "vd-listener.log");

    private readonly HwndSource _source;
    private readonly DispatcherTimer _poll;
    private int _lastIndex;
    private DateTime _lastResubscribe = DateTime.MinValue;
    private int _missed; // avisos perdidos desde la última re-suscripción (evidencia para el log)

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
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_VD_CHANGED)
        {
            Raise(lParam.ToInt32()); // lParam = índice del nuevo desktop
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Poll()
    {
        int current;
        try { current = VirtualDesktopAccessor.GetCurrentDesktopNumber(); }
        catch { return; } // la DLL/shell en un estado raro: el próximo tick reintenta
        if (current < 0 || current == _lastIndex) return;

        // El desk cambió y el aviso NO llegó (si hubiera llegado, _lastIndex ya valdría esto).
        _missed++;
        Raise(current);
        TryResubscribe();
    }

    private void Raise(int index)
    {
        _lastIndex = index;
        DesktopChanged?.Invoke(index);
    }

    private void TryResubscribe()
    {
        if (DateTime.Now - _lastResubscribe < ResubscribeCooldown) return;
        _lastResubscribe = DateTime.Now;

        int reg = -1;
        try
        {
            VirtualDesktopAccessor.UnregisterPostMessageHook(_source.Handle);
            VirtualDesktopAccessor.RestartVirtualDesktopAccessor();
            reg = VirtualDesktopAccessor.RegisterPostMessageHook(_source.Handle, WM_VD_CHANGED);
        }
        catch (Exception ex) { Log($"resubscribe FAILED: {ex.GetType().Name}: {ex.Message}"); return; }

        Log($"aviso perdido (x{_missed}) → re-suscripción, Register={reg}");
        _missed = 0;
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}"); }
        catch { /* el log es evidencia, nunca motivo para voltear la app */ }
    }

    public void Dispose()
    {
        _poll.Stop();
        try { VirtualDesktopAccessor.UnregisterPostMessageHook(_source.Handle); } catch { }
        _source.Dispose();
    }
}

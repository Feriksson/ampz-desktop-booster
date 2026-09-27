using System;
using System.Collections.Generic;
using System.Text;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Capa de alto nivel sobre VirtualDesktopAccessor.dll. Acá vive la lógica del legacy:
/// navegar por NOMBRE de desktop (MAIN / CONSOLES / MISCS / "DESK +N"), enviar ventanas,
/// ciclar entre los DESK+. La app no toca P/Invoke directo.
///
/// Diferencia clave con la primera versión del port: el legacy NO navega por índice fijo,
/// navega por fragmento de nombre — los desktops se identifican por cómo se llaman, no por
/// su posición. Eso es lo que hace que Win+Numpad7 sea SIEMPRE "MAIN" aunque lo muevas de lugar.
/// </summary>
public sealed class DesktopService
{
    public int Current => VirtualDesktopAccessor.GetCurrentDesktopNumber();
    public int Count => VirtualDesktopAccessor.GetDesktopCount();

    /// <summary>
    /// Resuelve el espacio activo de un desk. Lo inyecta App con ProjectStore.GetDeskProject;
    /// así DesktopService no depende de la capa de persistencia (queda desacoplado y testeable).
    /// </summary>
    public Func<int, string>? ProjectLookup { get; set; }

    /// <summary>
    /// Resuelve el CONTEXTO activo de un desk (nombre + color). Se inyecta igual que
    /// <see cref="ProjectLookup"/>, por la misma razón: DesktopService no conoce la persistencia.
    /// </summary>
    public Func<int, DeskModule>? ModuleLookup { get; set; }

    /// <summary>
    /// Nombre CRUDO del desktop por índice, tal cual lo devuelve la DLL — SIN el fallback de
    /// <see cref="GetName"/>. "" si Windows nunca le puso un nombre propio (el desktop por defecto,
    /// recién creado por el SO o por nuestro CreateDesktop, antes de un SetDesktopName).
    /// </summary>
    private string GetRawName(int index)
    {
        var buf = new byte[256];
        VirtualDesktopAccessor.GetDesktopName(index, buf, buf.Length);

        int nul = Array.IndexOf(buf, (byte)0);
        if (nul < 0) nul = buf.Length;

        var name = Encoding.UTF8.GetString(buf, 0, nul);
        return name.Length > 60 ? "" : name; // basura → tratamos como sin nombre
    }

    /// <summary>
    /// Nombre del desktop por índice. La DLL escribe UTF-8 (no PWSTR en la mayoría de builds);
    /// si sale basura o vacío, caemos a "Desktop N" — mismo fallback que el legacy.
    /// </summary>
    public string GetName(int index)
    {
        var name = GetRawName(index);
        return string.IsNullOrEmpty(name) ? $"Desktop {index + 1}" : name;
    }

    /// <summary>
    /// ¿Este desktop nunca recibió un nombre propio (ni de Windows ni de nosotros)? Lo usa el
    /// bootstrapper para ADOPTAR desktops "vírgenes" (recién instalado Windows, o creados por el SO
    /// sin renombrar) en vez de crear uno nuevo de más — pero nunca decide esto por posición: sólo
    /// dice si ESE índice puntual está libre de etiqueta.
    /// </summary>
    public bool IsUnnamedDefault(int index) => GetRawName(index) == "";

    /// <summary>Primer desktop cuyo nombre CONTIENE el fragmento (case-insensitive). -1 si no hay.</summary>
    public int FindByNameFragment(string fragment)
    {
        int count = Count;
        for (int i = 0; i < count; i++)
            if (GetName(i).Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Desktop con nombre EXACTO (case-insensitive). -1 si no existe. Para el bootstrap.</summary>
    public int FindExact(string name)
    {
        int count = Count;
        for (int i = 0; i < count; i++)
            if (string.Equals(GetName(i), name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Renombra el desktop por índice (UTF-8 null-terminado, como espera la DLL).</summary>
    public void SetName(int index, string name)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(name);
        var buf = new byte[utf8.Length + 1]; // + terminador nul
        Array.Copy(utf8, buf, utf8.Length);
        VirtualDesktopAccessor.SetDesktopName(index, buf);
    }

    // ── Historial de UN paso: "el desk de donde vengo" ────────────────────────────────────────
    // Alimenta el TOGGLE de vuelta (re-press del atajo del desk activo → volvés al anterior).
    // Es UNO solo y GLOBAL, no uno por desk: el modelo mental es el del Alt+Tab (ir y volver entre
    // DOS lugares), no un stack de navegación. Un "anterior" por desk suena más completo y en la
    // práctica confunde — te llevaría a un desk donde estuviste hace media hora, no al que acabás
    // de dejar, que es el ÚNICO que tenés en la cabeza cuando apretás para volver.
    private int _previous = -1;
    private int _lastKnown = -1; // dónde creemos estar; permite detectar el cambio y correr el previo

    /// <summary>Desktop del que venimos (-1 si todavía no hubo ningún cambio en esta sesión).</summary>
    public int Previous => _previous;

    /// <summary>
    /// Registra que AHORA estamos en <paramref name="index"/>, corriendo el anterior. Idempotente
    /// a propósito: lo llaman DOS caminos que se solapan — el <see cref="GoTo"/> (sincrónico, lo que
    /// hace que el re-press rafagueado lea un historial ya actualizado) y el DesktopChangeListener
    /// (asincrónico, la DLL postea el mensaje) que cubre TODO lo que no pasa por nosotros:
    /// Win+Ctrl+Flechas, la taskbar, la vista de tareas. Sin el segundo camino el "volver" te
    /// mandaría a un desk fantasma; sin el primero, una ráfaga leería el historial viejo.
    /// </summary>
    public void NoteCurrent(int index)
    {
        if (index < 0 || index == _lastKnown) return;
        if (_lastKnown >= 0) _previous = _lastKnown;
        _lastKnown = index;
    }

    /// <summary>Navega al desktop por índice (no-op si ya estamos ahí). false si el índice no existe.</summary>
    public bool GoTo(int index)
    {
        if (index < 0 || index >= Count) return false;

        int from = Current;
        if (from != index)
        {
            // Re-sincronizamos con la REALIDAD antes de saltar: si nos perdimos un cambio (por una
            // notificación de la DLL que todavía no llegó), _lastKnown estaría desfasado y el
            // "anterior" quedaría apuntando a un desk que ya no es de donde venimos.
            NoteCurrent(from);
            VirtualDesktopAccessor.GoToDesktopNumber(index);
            NoteCurrent(index);
        }
        return true;
    }

    /// <summary>Navega al desktop cuyo nombre contiene el fragmento. false si no existe.</summary>
    public bool GoToByName(string fragment)
    {
        int idx = FindByNameFragment(fragment);
        return idx >= 0 && GoTo(idx);
    }

    /// <summary>
    /// Mueve la ventana en primer plano al desktop con ese nombre y, si follow=true, salta ahí
    /// (el "enviar y seguir" — Win+Shift del legacy). false si el desktop no existe / no hay ventana.
    /// </summary>
    public bool SendForegroundWindowToByName(string fragment, bool follow = true)
    {
        int idx = FindByNameFragment(fragment);
        if (idx < 0) return false;

        IntPtr hwnd = WindowMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (Current == idx) return true;

        VirtualDesktopAccessor.MoveWindowToDesktopNumber(hwnd, idx);
        if (follow)
        {
            NoteCurrent(Current); // el "enviar y seguir" TAMBIÉN es un cambio de desk: entra al historial
            VirtualDesktopAccessor.GoToDesktopNumber(idx);
            NoteCurrent(idx);
        }
        return true;
    }

    /// <summary>Espacio activo del desktop (vía <see cref="ProjectLookup"/>), o "" si no hay.</summary>
    public string GetProject(int index) => ProjectLookup?.Invoke(index) ?? "";

    /// <summary>Contexto activo del desktop (vía <see cref="ModuleLookup"/>), o <see cref="DeskModule.None"/>.</summary>
    public DeskModule GetModule(int index) => ModuleLookup?.Invoke(index) ?? DeskModule.None;

    /// <summary>Envía una ventana ESPECÍFICA a un desktop por índice; si follow=true salta ahí.</summary>
    public bool SendWindowTo(IntPtr hwnd, int index, bool follow = true)
    {
        if (hwnd == IntPtr.Zero || index < 0 || index >= Count) return false;
        VirtualDesktopAccessor.MoveWindowToDesktopNumber(hwnd, index);
        if (follow)
        {
            NoteCurrent(Current); // idem: seguir a la ventana deja el desk de origen como "anterior"
            VirtualDesktopAccessor.GoToDesktopNumber(index);
            NoteCurrent(index);
        }
        return true;
    }

    /// <summary>
    /// Índice del desktop donde vive una ventana (-1 si la DLL no lo pudo resolver). Lo usa el
    /// servicio de atención: dado el PID que reclama, encuentra su ventana y pregunta acá su desk.
    /// Pasa por DesktopService a propósito — nadie más toca P/Invoke de desktops directo (la capa).
    /// </summary>
    public int GetWindowDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return -1;
        int idx = VirtualDesktopAccessor.GetWindowDesktopNumber(hwnd);
        return idx >= 0 && idx < Count ? idx : -1;
    }

    // ── Ciclo de vida de escritorios DINÁMICOS (espacio+contexto del launcher) ────────────────
    // A diferencia del bootstrap (que sólo CREA fijos al arrancar), estos los usa el launcher de
    // Win+NumpadEnter en caliente: crea uno al confirmar espacio/contexto, lo borra al re-press.

    /// <summary>
    /// Crea un escritorio nuevo al FINAL, espera a que el shell lo registre y devuelve su índice y
    /// GUID estable. El GUID es la identidad que el registro dinámico persiste — el índice se corre
    /// apenas se cree o borre OTRO desk, así que no sirve como key durable.
    /// </summary>
    public (int Index, Guid Id) CreateDesktopTracked()
    {
        VirtualDesktopAccessor.CreateDesktop();
        System.Threading.Thread.Sleep(60); // mismo margen que el bootstrapper: da tiempo al shell
        int index = Count - 1;
        Guid id = VirtualDesktopAccessor.GetDesktopIdByNumber(index);
        return (index, id);
    }

    /// <summary>Índice actual del desk por su GUID, o -1 si ya no existe (lo borraron por fuera).</summary>
    public int IndexOfId(Guid id) => VirtualDesktopAccessor.GetDesktopNumberById(id);

    /// <summary>GUID estable del desk en ese índice.</summary>
    public Guid IdOf(int index) => VirtualDesktopAccessor.GetDesktopIdByNumber(index);

    /// <summary>
    /// Borra el escritorio <paramref name="index"/>; sus ventanas pasan a <paramref name="fallbackIndex"/>.
    /// No mueve el foco por su cuenta — el llamador decide a dónde saltar después (normalmente, ya
    /// quedaste en el fallback porque Windows te deja ahí al borrar el desk activo).
    /// </summary>
    public void RemoveDesktopAt(int index, int fallbackIndex)
    {
        if (index < 0 || index >= Count || fallbackIndex < 0 || fallbackIndex >= Count || index == fallbackIndex)
            return;
        VirtualDesktopAccessor.RemoveDesktop(index, fallbackIndex);
        NoteCurrent(Current); // el borrado cambia el desk activo por debajo; resincronizamos el historial
    }
}

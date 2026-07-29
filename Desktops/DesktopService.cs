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
    /// Resuelve el proyecto activo de un desk. Lo inyecta App con ProjectStore.GetDeskProject;
    /// así DesktopService no depende de la capa de persistencia (queda desacoplado y testeable).
    /// </summary>
    public Func<int, string>? ProjectLookup { get; set; }

    /// <summary>
    /// Resuelve el MÓDULO activo de un desk (nombre + color). Se inyecta igual que
    /// <see cref="ProjectLookup"/>, por la misma razón: DesktopService no conoce la persistencia.
    /// </summary>
    public Func<int, DeskModule>? ModuleLookup { get; set; }

    /// <summary>
    /// Nombre del desktop por índice. La DLL escribe UTF-8 (no PWSTR en la mayoría de builds);
    /// si sale basura o vacío, caemos a "Desktop N" — mismo fallback que el legacy.
    /// </summary>
    public string GetName(int index)
    {
        var buf = new byte[256];
        VirtualDesktopAccessor.GetDesktopName(index, buf, buf.Length);

        int nul = Array.IndexOf(buf, (byte)0);
        if (nul < 0) nul = buf.Length;

        var name = Encoding.UTF8.GetString(buf, 0, nul);
        if (string.IsNullOrEmpty(name) || name.Length > 60)
            return $"Desktop {index + 1}";
        return name;
    }

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

    /// <summary>Navega al desktop por índice (no-op si ya estamos ahí). false si el índice no existe.</summary>
    public bool GoTo(int index)
    {
        if (index < 0 || index >= Count) return false;
        if (Current != index)
            VirtualDesktopAccessor.GoToDesktopNumber(index);
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
            VirtualDesktopAccessor.GoToDesktopNumber(idx);
        return true;
    }

    /// <summary>Proyecto activo del desktop (vía <see cref="ProjectLookup"/>), o "" si no hay.</summary>
    public string GetProject(int index) => ProjectLookup?.Invoke(index) ?? "";

    /// <summary>Módulo activo del desktop (vía <see cref="ModuleLookup"/>), o <see cref="DeskModule.None"/>.</summary>
    public DeskModule GetModule(int index) => ModuleLookup?.Invoke(index) ?? DeskModule.None;

    /// <summary>Envía una ventana ESPECÍFICA a un desktop por índice; si follow=true salta ahí.</summary>
    public bool SendWindowTo(IntPtr hwnd, int index, bool follow = true)
    {
        if (hwnd == IntPtr.Zero || index < 0 || index >= Count) return false;
        VirtualDesktopAccessor.MoveWindowToDesktopNumber(hwnd, index);
        if (follow)
            VirtualDesktopAccessor.GoToDesktopNumber(index);
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
}

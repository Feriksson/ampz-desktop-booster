using System.Runtime.InteropServices;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// P/Invoke a VirtualDesktopAccessor.dll — la MISMA DLL nativa que usaba el .ahk.
/// Reutilizarla (en vez de las COM interfaces IVirtualDesktopManager) nos ahorra el
/// infierno de que esas interfaces cambian entre builds de Windows. La DLL ya lo resuelve.
///
/// En x64 sólo hay una convención de llamada, así que no hace falta CallingConvention.
/// </summary>
internal static partial class VirtualDesktopAccessor
{
    private const string Dll = "VirtualDesktopAccessor.dll";

    [LibraryImport(Dll)]
    public static partial int GetCurrentDesktopNumber();

    [LibraryImport(Dll)]
    public static partial int GetDesktopCount();

    [LibraryImport(Dll)]
    public static partial void GoToDesktopNumber(int number);

    [LibraryImport(Dll)]
    public static partial void MoveWindowToDesktopNumber(IntPtr hwnd, int number);

    [LibraryImport(Dll)]
    public static partial int GetWindowDesktopNumber(IntPtr hwnd);

    [LibraryImport(Dll)]
    public static partial int IsWindowOnDesktopNumber(IntPtr hwnd, int number);

    /// <summary>
    /// Escribe el nombre del desktop (UTF-8, no PWSTR en la mayoría de builds) en <paramref name="name"/>.
    /// El caller decodifica el buffer. length = capacidad del buffer en bytes.
    /// ⚠ length es <c>usize</c> en la DLL (Ciantic: <c>out_utf8_len: usize</c>) → <c>nuint</c> acá, NO
    /// <c>int</c>: con int, en x64 los 32 bits altos del registro quedan con basura y la DLL lee una
    /// capacidad arbitraria del buffer.
    /// </summary>
    [LibraryImport(Dll)]
    public static partial int GetDesktopName(int index, [Out] byte[] name, nuint length);

    /// <summary>Renombra el desktop por índice. name = UTF-8 null-terminado. Devuelve 1 si OK.</summary>
    [LibraryImport(Dll)]
    public static partial int SetDesktopName(int index, [In] byte[] name);

    /// <summary>
    /// Crea un escritorio virtual nuevo al final. NO cambia el foco (a diferencia de Win+Ctrl+D),
    /// lo cual es ideal para el bootstrap silencioso. Confirmado exportado por esta DLL.
    /// </summary>
    [LibraryImport(Dll)]
    public static partial int CreateDesktop();

    /// <summary>
    /// Registra una ventana para recibir un PostMessage cada vez que cambia el desktop virtual
    /// (por hotkey, Win+Ctrl+Flechas, taskbar, lo que sea). messageOffset es el id de mensaje
    /// que la DLL postea; lParam del mensaje = índice del nuevo desktop.
    /// </summary>
    [LibraryImport(Dll)]
    public static partial int RegisterPostMessageHook(IntPtr listenerHwnd, int messageOffset);

    /// <summary>
    /// Baja la suscripción de <see cref="RegisterPostMessageHook"/>. Sin esto, cada salida (y peor,
    /// cada crash) dejaba una suscripción colgada del lado del shell.
    /// </summary>
    [LibraryImport(Dll)]
    public static partial int UnregisterPostMessageHook(IntPtr listenerHwnd);

    // ⛔ RestartVirtualDesktopAccessor (exportada) NO se declara a propósito: llamarla en caliente
    // (Unregister → Restart → Register, para revivir avisos perdidos) crasheó la app al instante bajo
    // cdb con "Free Heap block modified after it was freed" — libera objetos internos que el thread
    // de notificaciones de la DLL sigue usando. Ver DesktopChangeListener.ReportMissed.

    /// <summary>
    /// "Pinea" la ventana a TODOS los escritorios virtuales: pasa a estar visible en cualquier
    /// desktop, sin importar dónde se haya creado. Es lo que hace que el overlay aparezca en el
    /// desktop al que saltás (una ventana sin pinear vive sólo en el desktop donde se mostró).
    /// </summary>
    [LibraryImport(Dll)]
    public static partial void PinWindow(IntPtr hwnd);

    /// <summary>
    /// Elimina el escritorio <paramref name="removeDesktopNumber"/>; sus ventanas pasan al
    /// <paramref name="fallbackDesktopNumber"/> (firma Ciantic: remove_desktop_number,
    /// fallback_desktop_number). Es el complemento de <see cref="CreateDesktop"/> para el ciclo de
    /// vida de los escritorios DINÁMICOS (espacio+contexto) del launcher — un desk fijo del catálogo
    /// nunca se borra por acá, sólo los que crea Win+NumpadEnter.
    /// </summary>
    [LibraryImport(Dll)]
    public static partial int RemoveDesktop(int removeDesktopNumber, int fallbackDesktopNumber);

    /// <summary>
    /// GUID estable del escritorio por índice (Ciantic lo devuelve POR VALOR, 16 bytes blittable).
    /// Es la identidad que sobrevive a que otros índices se corran al crear/borrar desks — por eso el
    /// registro de escritorios dinámicos indexa por este GUID y no por el índice de sesión.
    /// </summary>
    [LibraryImport(Dll)]
    public static partial Guid GetDesktopIdByNumber(int number);

    /// <summary>Índice ACTUAL de un escritorio dado su GUID, o -1 si ya no existe (fue borrado).</summary>
    [LibraryImport(Dll)]
    public static partial int GetDesktopNumberById(Guid desktopId);
}

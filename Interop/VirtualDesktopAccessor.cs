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
    /// </summary>
    [LibraryImport(Dll)]
    public static partial int GetDesktopName(int index, [Out] byte[] name, int length);

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
    /// "Pinea" la ventana a TODOS los escritorios virtuales: pasa a estar visible en cualquier
    /// desktop, sin importar dónde se haya creado. Es lo que hace que el overlay aparezca en el
    /// desktop al que saltás (una ventana sin pinear vive sólo en el desktop donde se mostró).
    /// </summary>
    [LibraryImport(Dll)]
    public static partial void PinWindow(IntPtr hwnd);
}

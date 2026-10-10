using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// ¿Este proceso corre elevado (token de administrador)? Centralizado acá porque lo necesitan dos
/// consumidores sin relación entre sí: <see cref="Services.AutoStartService"/> (crear la tarea
/// programada con RunLevel Highest requiere admin) y <see cref="UnelevatedLauncher"/> (decidir si
/// hay que des-elevar a los hijos). Un solo lugar evita que definan el criterio cada uno por su lado.
/// </summary>
internal static partial class ElevationHelper
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20; // TOKEN_INFORMATION_CLASS: devuelve un TOKEN_ELEVATION (un DWORD)

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out uint tokenInformation, int tokenInformationLength, out int returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// ¿El proceso <paramref name="pid"/> corre con token elevado? <c>null</c> = NO SE PUDO SABER
    /// (proceso muerto, acceso denegado). Devuelve tri-estado a propósito: el consumidor
    /// (<c>Shell</c>, para decidir si una ventana de Windows Terminal es reusable) tiene que poder
    /// tratar "no sé" como "no coincide" — adivinar un bool acá metería una pestaña en la ventana
    /// del OTRO lado de la elevación, o ninguna. Corriendo elevados (el caso normal, tarea
    /// programada) podemos consultar cualquier proceso del usuario; sin elevar, el token de un
    /// proceso elevado suele dar acceso denegado → <c>null</c>.
    /// </summary>
    public static bool? IsProcessElevated(uint pid)
    {
        if (pid == 0) return null;

        IntPtr hProcess = IntPtr.Zero, hToken = IntPtr.Zero;
        try
        {
            hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return null;
            if (!OpenProcessToken(hProcess, TOKEN_QUERY, out hToken)) return null;
            if (!GetTokenInformation(hToken, TokenElevation, out uint isElevated, sizeof(uint), out _)) return null;
            return isElevated != 0;
        }
        catch { return null; }
        finally
        {
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    /// <summary>Lo mismo que <see cref="IsProcessElevated"/>, para el proceso DUEÑO de una ventana.</summary>
    public static bool? IsWindowProcessElevated(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hWnd, out uint pid);
        return IsProcessElevated(pid);
    }

    /// <summary>true si el proceso actual corre con el token elevado (Administrador).</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // Si no podemos preguntar, asumimos que NO estamos elevados: es el camino más
            // conservador (el que menos sorprende — nunca de-elevamos algo que ya corría normal).
            return false;
        }
    }
}

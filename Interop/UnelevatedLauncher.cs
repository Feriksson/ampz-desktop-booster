using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// Punto ÚNICO de salida para todo lo que esta app lanza. Con la migración a la tarea programada
/// (ver <see cref="Services.AutoStartService"/>) la app corre ELEVADA de punta a punta — y sin este
/// helper, TODO lo que abrís desde acá (VS Code, una terminal, "Abrir con", el panel Docker,
/// Explorer, Descargas, una URL, un comando de servicio) heredaría el token de administrador. Eso
/// es justo lo que el usuario NO quiere: un doble-click en un link que abre el browser elevado, o
/// un editor de código corriendo con privilegios que ni el propio VS Code pide.
///
/// TÉCNICA (la misma que usan Start11/ExplorerPatcher y demás "de-elevators"): CreateProcessW con
/// <c>PROC_THREAD_ATTRIBUTE_PARENT_PROCESS</c> apuntando al handle de explorer.exe (el shell del
/// usuario, que SIEMPRE corre a integridad MEDIA aunque nosotros estemos elevados). El hijo hereda
/// el TOKEN del "padre" declarado, no el del proceso que llama a CreateProcess — es la única vía
/// soportada por Windows para bajar de admin a medio sin pedirle la contraseña al usuario ni
/// spawnear un proceso intermedio no-elevado nuestro (que de entrada no podríamos crear: un proceso
/// elevado no puede lanzar uno no-elevado por CreateProcess normal, HEREDA su propio token).
///
/// Si NO estamos elevados (deprecated → normal), este helper es un Process.Start de siempre: CERO
/// cambio de comportamiento para quien instaló la app sin la tarea programada, o la sigue corriendo
/// manual. Y si el camino de-elevado falla por lo que sea (falta el shell, cambia una API, lo que
/// sea), cae al Process.Start elevado de toda la vida — preferimos un hijo elevado (una regresión
/// de seguridad, no de funcionalidad) a que la acción del usuario no haga NADA.
/// </summary>
internal static partial class UnelevatedLauncher
{
    // ── Constantes Win32 ──────────────────────────────────────────────────────
    private const uint PROCESS_CREATE_PROCESS = 0x0080;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = 0x00020000;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, ref IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    // ── API pública ───────────────────────────────────────────────────────────

    /// <summary>Lanza <paramref name="psi"/>. De-elevado si esta app corre admin; normal si no.</summary>
    public static Process? Start(ProcessStartInfo psi)
    {
        if (!ElevationHelper.IsElevated())
            return Process.Start(psi);

        try
        {
            if (TryStartDeelevated(psi, out var proc))
                return proc;
        }
        catch { /* cualquier fallo del camino nativo → fallback de abajo, ver el summary de la clase */ }

        // Fallback CONSCIENTE: el hijo nace elevado (no ideal) pero la acción del usuario SÍ pasa
        // algo, que es preferible a un botón que no responde.
        try { return Process.Start(psi); }
        catch { return null; }
    }

    /// <summary>
    /// Para targets sin .exe propio — un archivo por asociación, una carpeta, una URL, un
    /// <c>ms-settings:</c> — que necesitan que el SHELL resuelva el verbo. Lo envolvemos en
    /// "explorer.exe &lt;target&gt;": ese explorer nuevo nace de-elevado (mismo mecanismo de arriba)
    /// y es él quien resuelve la asociación y lanza el handler real, que hereda SU token, no el
    /// nuestro. Evita re-implementar IShellDispatch2/ShellExecuteEx a mano para un caso que
    /// "explorer.exe" ya resuelve gratis.
    /// </summary>
    public static Process? StartShellTarget(string target) =>
        Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{target}\"", UseShellExecute = false });

    // ── Implementación ────────────────────────────────────────────────────────

    private static bool TryStartDeelevated(ProcessStartInfo psi, out Process? process)
    {
        process = null;

        IntPtr shellHwnd = GetShellWindow();
        if (shellHwnd == IntPtr.Zero) return false; // sin shell vivo no hay de quién copiar el token

        GetWindowThreadProcessId(shellHwnd, out uint shellPid);
        if (shellPid == 0) return false;

        IntPtr hShellProcess = OpenProcess(PROCESS_CREATE_PROCESS | PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
        if (hShellProcess == IntPtr.Zero) return false;

        IntPtr attrList = IntPtr.Zero;
        try
        {
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                return false;

            IntPtr parentHandle = hShellProcess;
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                    ref parentHandle, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                return false;

            var si = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                lpAttributeList = attrList,
            };

            string commandLine = BuildCommandLine(psi);
            string? workingDir = string.IsNullOrWhiteSpace(psi.WorkingDirectory) ? null : psi.WorkingDirectory;
            IntPtr envBlock = BuildEnvironmentBlock(psi);

            uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;

            try
            {
                bool ok = CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                    envBlock, workingDir, ref si, out var pi);
                if (!ok) return false;

                CloseHandle(pi.hThread);
                try { process = Process.GetProcessById(pi.dwProcessId); }
                catch { /* el proceso puede haber muerto ya (spawn-and-exit) — igual fue un lanzamiento OK */ }
                CloseHandle(pi.hProcess);
                return true;
            }
            finally
            {
                if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            }
        }
        finally
        {
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            CloseHandle(hShellProcess);
        }
    }

    /// <summary>
    /// "<c>exe</c> arg1 arg2 ..." con el quoting Win32 estándar. Prioriza <c>ArgumentList</c> (lo que
    /// usa la mayoría de los call sites de este repo, ver Shell.cs) sobre <c>Arguments</c> crudo.
    /// </summary>
    private static string BuildCommandLine(ProcessStartInfo psi)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(psi.FileName));

        if (psi.ArgumentList.Count > 0)
        {
            foreach (var a in psi.ArgumentList) { sb.Append(' '); sb.Append(Quote(a)); }
        }
        else if (!string.IsNullOrEmpty(psi.Arguments))
        {
            sb.Append(' ');
            sb.Append(psi.Arguments); // ya viene pre-armado (con sus propias comillas) por el caller
        }

        return sb.ToString();
    }

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            return arg; // sin espacios/comillas no hace falta nada
        // Escape Win32 estándar: comillas se escapan con \", y una racha de \ que precede a una
        // comilla se duplica (ver la doc de CommandLineToArgvW).
        var sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes); backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Bloque de entorno UTF-16 double-null-terminated para CreateProcessW, a partir de
    /// <c>psi.Environment</c> — que ya trae aplicada cualquier limpieza que el caller haya hecho
    /// (el scrub de VSCODE_*/ELECTRON_* de Shell.cs/AppCatalog/BrowserShim, documentado en el
    /// CLAUDE.md del repo). Ordenado (no es obligatorio para CreateProcess, pero es la convención de
    /// Windows y evita sorpresas con herramientas que asumen bloques ordenados).
    /// </summary>
    private static IntPtr BuildEnvironmentBlock(ProcessStartInfo psi)
    {
        var pairs = psi.Environment
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value}");

        var sb = new StringBuilder();
        foreach (var p in pairs) { sb.Append(p); sb.Append('\0'); }
        sb.Append('\0');

        return Marshal.StringToHGlobalUni(sb.ToString());
    }
}

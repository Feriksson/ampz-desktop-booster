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

    /// <summary>
    /// Lanza <paramref name="psi"/>. De-elevado si esta app corre admin; normal si no.
    /// <para>
    /// <paramref name="viaTrampoline"/> es OBLIGATORIO para apps Chromium/Electron (VS Code, Brave,
    /// Discord, Slack...). BUG cazado con un banco de pruebas que replica este CreateProcessW
    /// (2026-09-25): un Code.exe que nace DIRECTO con PROC_THREAD_ATTRIBUTE_PARENT_PROCESS muere en
    /// el arranque temprano con exit code 0xFFFF7001, antes de escribir una línea de log — sin
    /// ventana y sin error visible (Win+F2 "no hacía nada"). No era el env (se probó heredado y
    /// armado a mano), ni CREATE_NO_WINDOW, ni integridad (todo a media). Lo que SÍ anda: que el
    /// de-elevado sea un <c>cmd.exe</c> intermedio y que ÉL lance la app — Chromium nace como hijo
    /// normal de un proceso que ya tiene integridad media, sin padre "adoptado".
    /// </para>
    /// <para>
    /// También es el camino de <c>wt.exe</c> (ver <c>Apps/Shell.cs</c>). HALLAZGO 2026-10-10: lanzado
    /// DIRECTO con el atributo de padre, el Windows Terminal resultante (y todas sus pestañas de pwsh)
    /// salía ELEVADO igual. <c>wt.exe</c> es un App Execution Alias y su activación AppX no garantiza
    /// respetar al padre declarado; con el trampolín el que nace de-elevado es un cmd.exe común y es
    /// ÉL quien activa el alias, ya con token de integridad media de verdad.
    /// </para>
    /// <para>
    /// La caída al camino elevado dejó de ser MUDA: ese mismo día no se pudo distinguir "el camino
    /// de-elevado falló y caímos al fallback" de "anduvo pero el alias ignoró al padre", porque el
    /// <c>catch</c> se tragaba todo. Hoy cada caída deja una línea en <see cref="LogFileName"/>.
    /// </para>
    /// </summary>
    public static Process? Start(ProcessStartInfo psi, bool viaTrampoline = false)
    {
        if (!ElevationHelper.IsElevated())
            return Process.Start(psi);

        string failure;
        try
        {
            // Con trampolín, el Process devuelto es el cmd efímero, no la app: ningún caller que
            // lo pida lo usa para nada más que "¿arrancó?".
            string commandLine = viaTrampoline ? BuildTrampolineCommandLine(psi) : BuildCommandLine(psi);

            // cmd.exe corta su línea en 8191 caracteres (CreateProcess banca 32767). Un lote grande
            // de pestañas con sus comandos en base64 puede pasarse: ahí el trampolín lanzaría un
            // comando TRUNCADO, que es peor que no usarlo. Se va por el camino directo y se anota,
            // porque para wt.exe ese camino es justo el que puede terminar elevado.
            if (viaTrampoline && commandLine.Length > MaxCmdLineLength)
            {
                LogFailure(psi.FileName, $"trampolín omitido: la línea de {commandLine.Length} caracteres " +
                    "supera el límite de cmd.exe → se usa el camino directo (puede nacer ELEVADO si es un alias AppX)");
                commandLine = BuildCommandLine(psi);
            }

            if (TryStartDeelevated(psi, commandLine, out var proc, out failure))
                return proc;
        }
        catch (Exception ex)
        {
            failure = $"excepción {ex.GetType().Name}: {ex.Message}";
        }

        // Fallback CONSCIENTE: el hijo nace elevado (no ideal) pero la acción del usuario SÍ pasa
        // algo, que es preferible a un botón que no responde. Pero se ANOTA: una regresión de
        // seguridad silenciosa es la que nadie arregla.
        LogFailure(psi.FileName, $"{failure} (trampolín={viaTrampoline}) → fallback a Process.Start ELEVADO");
        try { return Process.Start(psi); }
        catch (Exception ex)
        {
            LogFailure(psi.FileName, $"el fallback elevado TAMBIÉN falló: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ── Registro de caídas ────────────────────────────────────────────────────

    /// <summary>Nombre del log (en <see cref="Persistence.AppPaths.DataDir"/>) de las caídas al camino elevado.</summary>
    public const string LogFileName = "unelevated-launch.log";

    // Tope del log. Cada línea es un evento raro (una caída), así que 256 KB son años de uso normal;
    // el tope existe para el caso patológico (un lazo que falla siempre) y al pasarlo se conserva
    // sólo la cola, que es lo que importa al diagnosticar.
    private const long LogMaxBytes = 256 * 1024;
    private const int LogKeepChars = 64 * 1024;

    // Margen bajo el límite real de cmd.exe (8191), ver Start.
    private const int MaxCmdLineLength = 8000;

    private static readonly object LogLock = new();

    /// <summary>
    /// Agrega UNA línea al log de lanzamientos. Jamás tira (regla del repo: la persistencia nunca
    /// voltea la app): si el disco falla, se pierde la línea y el lanzamiento sigue igual. Es
    /// <c>internal</c> porque <c>Shell</c> anota acá las negativas de su camino elevado — un solo
    /// archivo para "qué pasó con el token de lo que lancé".
    /// </summary>
    internal static void LogFailure(string fileName, string detail)
    {
        try
        {
            lock (LogLock)
            {
                string path = System.IO.Path.Combine(Persistence.AppPaths.DataDir, LogFileName);

                var info = new System.IO.FileInfo(path);
                if (info.Exists && info.Length > LogMaxBytes)
                {
                    string all = System.IO.File.ReadAllText(path);
                    System.IO.File.WriteAllText(path, all[^Math.Min(all.Length, LogKeepChars)..]);
                }

                System.IO.File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {fileName} | {detail}{Environment.NewLine}");
            }
        }
        catch { /* sin log seguimos: lo que no puede pasar es que anotar una falla rompa el lanzamiento */ }
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

    /// <summary>
    /// <paramref name="failure"/> nombra el PASO que falló y su código Win32 — es lo que termina en el
    /// log. Antes eran cinco <c>return false</c> pelados e indistinguibles entre sí. El código se lee
    /// PEGADO a la llamada que falló: cualquier P/Invoke posterior (hasta un CloseHandle) lo pisa.
    /// </summary>
    private static bool TryStartDeelevated(ProcessStartInfo psi, string commandLine, out Process? process,
                                           out string failure)
    {
        process = null;
        failure = "";

        IntPtr shellHwnd = GetShellWindow();
        if (shellHwnd == IntPtr.Zero)
        {
            failure = "GetShellWindow devolvió 0 (no hay shell vivo de quién copiar el token)";
            return false;
        }

        GetWindowThreadProcessId(shellHwnd, out uint shellPid);
        if (shellPid == 0)
        {
            failure = "GetWindowThreadProcessId no resolvió el PID del shell";
            return false;
        }

        IntPtr hShellProcess = OpenProcess(PROCESS_CREATE_PROCESS | PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
        if (hShellProcess == IntPtr.Zero)
        {
            failure = $"OpenProcess(shell pid {shellPid}) err={Marshal.GetLastWin32Error()}";
            return false;
        }

        IntPtr attrList = IntPtr.Zero;
        try
        {
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
            {
                failure = $"InitializeProcThreadAttributeList err={Marshal.GetLastWin32Error()}";
                return false;
            }

            IntPtr parentHandle = hShellProcess;
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                    ref parentHandle, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                failure = $"UpdateProcThreadAttribute(PARENT_PROCESS) err={Marshal.GetLastWin32Error()}";
                return false;
            }

            var si = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                lpAttributeList = attrList,
            };

            string? workingDir = string.IsNullOrWhiteSpace(psi.WorkingDirectory) ? null : psi.WorkingDirectory;
            IntPtr envBlock = BuildEnvironmentBlock(psi);

            uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;

            try
            {
                bool ok = CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                    envBlock, workingDir, ref si, out var pi);
                if (!ok)
                {
                    failure = $"CreateProcessW err={Marshal.GetLastWin32Error()}";
                    return false;
                }

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
    private static string BuildCommandLine(ProcessStartInfo psi, bool forCmd = false)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(psi.FileName, forCmd));

        if (psi.ArgumentList.Count > 0)
        {
            foreach (var a in psi.ArgumentList) { sb.Append(' '); sb.Append(Quote(a, forCmd)); }
        }
        else if (!string.IsNullOrEmpty(psi.Arguments))
        {
            sb.Append(' ');
            sb.Append(psi.Arguments); // ya viene pre-armado (con sus propias comillas) por el caller
        }

        return sb.ToString();
    }

    /// <summary>
    /// "<c>cmd.exe /d /c start "" &lt;comando&gt;</c>": el cmd (oculto por CREATE_NO_WINDOW, y /d
    /// para que no corra AutoRun del registro) le pasa la posta a <c>start</c>, que lanza la app
    /// desacoplada y el cmd sale al instante. La app hereda el env del cmd — que es el bloque que
    /// armamos desde <c>psi.Environment</c>, con el scrub de VSCODE_*/ELECTRON_* ya aplicado — y su
    /// working dir. Límite asumido: cmd expande <c>%VAR%</c> aunque esté entre comillas; un path real
    /// con un <c>%algo%</c> que coincida con una variable definida es rarísimo.
    ///
    /// La línea pasa por el PARSER de cmd antes de llegar a la app, así que los argumentos se arman
    /// con <c>forCmd</c> — ver <see cref="Quote"/>. Dos límites más, asumidos: (1) <c>Arguments</c>
    /// crudo viaja tal cual (lo pre-armó el caller, no hay forma de re-citarlo sin romperlo); (2) un
    /// argumento con una comilla ADENTRO se escapa como <c>\"</c> para la app, pero cmd no entiende ese
    /// escape y cuenta la comilla — lo que le siga queda fuera de comillas para él. Ningún call site
    /// del trampolín pasa hoy un argumento así (paths y base64 no llevan comillas).
    /// </summary>
    private static string BuildTrampolineCommandLine(ProcessStartInfo psi) =>
        "cmd.exe " + BuildTrampolineArguments(psi);

    private static string BuildTrampolineArguments(ProcessStartInfo psi) =>
        "/d /c start \"\" " + BuildCommandLine(psi, forCmd: true);

    /// <summary>
    /// Los argumentos de cmd.exe del trampolín, para quien necesite lanzar ESE cmd por otra vía que
    /// CreateProcess — hoy <c>Shell</c>, que lo levanta con el verbo <c>runas</c> cuando piden una
    /// terminal elevada y la app no lo está. <c>null</c> si la línea no entra en el límite de cmd:
    /// el caller decide qué hacer, acá no se lanza nada truncado.
    /// </summary>
    internal static string? TryBuildTrampolineArguments(ProcessStartInfo psi)
    {
        string args = BuildTrampolineArguments(psi);
        return "cmd.exe ".Length + args.Length > MaxCmdLineLength ? null : args;
    }

    // Metacaracteres que cmd interpreta FUERA de comillas. Probado el 2026-10-10 con un programa que
    // hace eco de su argv detrás de `cmd /d /c start ""`: un argumento sin comillas con `^` llegaba
    // SIN el caret (cmd se lo come como escape), y `& | < >` directamente parten o redirigen el
    // comando. Entre comillas todos son literales. Los paréntesis entran por las dudas (dentro de un
    // bloque de cmd cierran el bloque). Quedan AFUERA a propósito:
    //   `;` `,` `=`  → verificado que pasan intactos, y el `;` SUELTO tiene que viajar pelado: es el
    //                  separador de pestañas de wt.exe, que entre comillas dejaría de reconocerlo.
    //   `%`          → cmd lo expande también entre comillas; citarlo no arregla nada (límite
    //                  documentado en BuildTrampolineCommandLine).
    private static readonly char[] CmdMetaChars = ['&', '|', '<', '>', '^', '(', ')'];

    /// <summary>
    /// <paramref name="forCmd"/>: además de espacios/comillas, cita el argumento si trae un
    /// metacaracter de cmd. Sólo para el trampolín — en el camino directo la línea va a
    /// CreateProcess sin pasar por ningún shell, y ahí esas comillas de más no aportan nada.
    /// </summary>
    private static string Quote(string arg, bool forCmd = false)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0
            && !(forCmd && arg.IndexOfAny(CmdMetaChars) >= 0))
            return arg; // sin espacios/comillas (ni metacaracteres de cmd) no hace falta nada
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

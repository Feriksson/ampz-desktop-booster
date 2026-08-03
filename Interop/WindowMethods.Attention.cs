using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// Resolución PID → ventana top-level, para el widget de "atención por desk". El cliente (un hook
/// de Claude, mañana cualquier otro integrador) postea SU propio PID; nosotros lo traducimos a la
/// ventana real que lo hospeda para después preguntarle a la DLL en qué escritorio virtual cae.
///
/// El nudo: el proceso que dispara el aviso (bash/powershell del hook) casi nunca TIENE ventana —
/// vive DENTRO de la terminal de Claude Code, que vive dentro de VS Code / Windows Terminal, que es
/// quien tiene la ventana top-level. Por eso subimos el ÁRBOL DE PROCESOS (Toolhelp32) hasta hallar
/// un ancestro con ventana top-level real. El PID es único, así que esto desambigua aunque el mismo
/// espacio esté abierto en dos desks: cada instancia es un proceso distinto en un árbol distinto.
/// </summary>
internal static partial class WindowMethods
{
    // ── Toolhelp32: foto de TODOS los procesos para armar el mapa hijo→padre de un saque ──
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    // CharSet.Unicode → el runtime resuelve a Process32FirstW/NextW automáticamente.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Conjunto del PID dado MÁS toda su cadena de ancestros (incluido él mismo). Una sola foto de
    /// Toolhelp32 arma el mapa hijo→padre y subimos hasta la raíz. El guard de "visitados" corta
    /// cualquier ciclo patológico de PIDs reciclados (Windows reusa PIDs) para no colgarnos.
    /// </summary>
    private static HashSet<uint> ProcessAncestry(uint pid)
    {
        var ancestry = new HashSet<uint> { pid };

        var childToParent = new Dictionary<uint, uint>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == new IntPtr(-1)) return ancestry; // INVALID_HANDLE_VALUE → devolvemos solo el pid

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref entry))
            {
                do { childToParent[entry.th32ProcessID] = entry.th32ParentProcessID; }
                while (Process32Next(snap, ref entry));
            }
        }
        finally { CloseHandle(snap); }

        uint cur = pid;
        while (childToParent.TryGetValue(cur, out uint parent) && parent != 0 && ancestry.Add(parent))
            cur = parent;

        return ancestry;
    }

    /// <summary>PID del proceso dueño de una ventana (0 si no se pudo). Para el self-test de atención.</summary>
    public static uint PidOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    /// <summary>
    /// TODAS las ventanas top-level REALES cuyo proceso dueño esté en el árbol del <paramref name="pid"/>
    /// (él o un ancestro). Lista vacía si ninguna (proceso headless sin ventana en ningún lado).
    ///
    /// Devolvemos TODAS, no la primera, por un motivo concreto: un host Electron (VS Code) con varias
    /// ventanas comparte el PID de su proceso "main" — TODAS sus ventanas reportan ese mismo PID. Si
    /// cortáramos en la primera, con dos ventanas de VS Code abríamos el aviso en el desk equivocado.
    /// El caller desambigua entre estas candidatas (por el cwd / título de la ventana).
    ///
    /// NO filtramos cloaked a propósito: una ventana en OTRO escritorio virtual figura cloaked, y ése
    /// es JUSTO el caso que queremos cazar — el aviso suele llegar mientras estás en otro desk. La DLL
    /// resuelve el desk de una ventana esté donde esté.
    /// </summary>
    public static List<IntPtr> TopLevelWindowsForPid(int pid)
    {
        var ancestry = ProcessAncestry((uint)pid);

        var found = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsRealTopLevel(hwnd)) return true;     // diálogos, popups, tool windows → seguir
            GetWindowThreadProcessId(hwnd, out uint wpid);
            if (ancestry.Contains(wpid)) found.Add(hwnd);
            return true;                                // seguir: queremos TODAS las candidatas
        }, IntPtr.Zero);
        return found;
    }
}

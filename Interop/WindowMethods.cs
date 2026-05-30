using System.Runtime.InteropServices;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// Win32 para la ventana activa, el "masking" de la tecla Win y el control de NumLock.
/// </summary>
internal static partial class WindowMethods
{
    /// <summary>
    /// Firma propia que ponemos en dwExtraInfo cuando NOSOTROS inyectamos input.
    /// El hook la reconoce y deja pasar esos eventos sin procesarlos (evita reentrada).
    /// </summary>
    public static readonly IntPtr InjectedSignature = (IntPtr)0x1A2B3C;

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_NUMLOCK = 0x90;
    public const ushort VK_LWIN    = 0x5B;
    public const ushort VK_D       = 0x44;

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int nVirtKey);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(IntPtr hWnd, IntPtr lpString, int nMaxCount);

    // ── Estilos extendidos de ventana (para sacar el overlay del taskbar/Alt-Tab y que no robe foco) ──
    public const int GWL_EXSTYLE      = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;  // fuera del Alt-Tab y del taskbar
    public const int WS_EX_NOACTIVATE = 0x08000000;  // no roba el foco al mostrarse

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ── Enumerar ventanas + activarlas (para "Downloads desktop-aware") ──
    // DllImport (no LibraryImport): el delegate y el StringBuilder no van bien con source-gen.

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>true si la ventana está MINIMIZADA (iconizada).</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_RESTORE = 9;

    // ── Forzar foreground confiable para ventanas propias disparadas por hotkey global ──
    // (GetWindowThreadProcessId ya está declarado en el partial Governance — lo reusamos.)

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>
    /// Trae una ventana PROPIA al primer plano y le da foco de teclado de forma confiable,
    /// aun cuando la dispara un hotkey global (en ese instante NUESTRO proceso no es el
    /// foreground). Windows bloquea <see cref="SetForegroundWindow"/> para procesos que no son
    /// el foreground (anti-robo de foco), así que <c>Activate()</c> a secas no alcanza la primera
    /// vez. Truco canónico: enganchamos nuestro hilo de input al del foreground actual con
    /// <see cref="AttachThreadInput"/> → compartimos estado de input → Windows ya nos deja hacer
    /// el cambio. Desenganchamos SIEMPRE al final (finally), pase lo que pase.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd, bool preserveMaximized = false)
    {
        if (hwnd == IntPtr.Zero) return;

        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0u : GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();

        bool attached = fgThread != 0 && fgThread != thisThread
            && AttachThreadInput(thisThread, fgThread, true);
        try
        {
            // SW_RESTORE des-MAXIMIZA una ventana maximizada (la lleva a tamaño normal). Para los
            // singletons propios queremos eso (re-press → des-minimizar). Pero al reusar una ventana
            // ajena ya maximizada (ej. Windows Terminal) NO debemos tocarle el tamaño: con
            // preserveMaximized solo des-minimizamos si REALMENTE vino iconizada.
            if (!preserveMaximized || IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, fgThread, false);
        }
    }

    /// <summary>Texto de una ventana cualquiera (título) — vacío si no se pudo leer.</summary>
    public static string TextOf(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(512);
        int n = GetWindowTextW(hWnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    /// <summary>Clase de una ventana — vacío si no se pudo leer.</summary>
    public static string ClassOf(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        int n = GetClassName(hWnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    /// <summary>Título de la ventana en primer plano (vacío si no hay o no se puede leer).</summary>
    public static string GetActiveWindowTitle()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return "";

        // Buffer nativo (UTF-16). LibraryImport no marshala char[] sin opt-in, así que
        // manejamos la memoria a mano — el patrón canónico para GetWindowTextW con source-gen.
        const int cap = 512;
        IntPtr buf = Marshal.AllocHGlobal(cap * sizeof(char));
        try
        {
            int len = GetWindowText(hwnd, buf, cap);
            return len > 0 ? Marshal.PtrToStringUni(buf, len) : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;          // 1 = INPUT_KEYBOARD
        public InputUnion U;
    }

    // OJO: el union TIENE que tener el tamaño de su miembro MÁS GRANDE (MOUSEINPUT), aunque sólo
    // usemos KEYBDINPUT. Si no, Marshal.SizeOf<INPUT>() da 32 en x64 en vez de los 40 reales, y
    // SendInput rechaza el cbSize y devuelve 0 (ERROR_INVALID_PARAMETER 87) → el masking nunca
    // entraba y el menú Inicio se abría con Win+`. Mantener mi y hi para clavar el layout real.
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// Suelta la tecla Win ENMASCARADA: inyecta Ctrl down+up e inmediatamente después el Win-up,
    /// todo en un solo <c>SendInput</c> (orden garantizado dentro del batch). Así el shell ve un
    /// Ctrl JUSTO antes del Win-up y no lo interpreta como "tap de Win solo" → no abre el menú Inicio.
    ///
    /// Es lo que hace AutoHotkey: enmascara en el RELEASE de la Win, no en el press. El mask viejo
    /// (Ctrl tap en el down del hotkey) fallaba porque para cuando llegaba el Win-up ya estaba viejo
    /// — el shell mira el evento inmediatamente anterior al Win-up, y entremedio pasaba el key-up del
    /// hotkey + tiempo muerto. El caller TRAGA el Win-up físico y deja que éste lo reponga.
    ///
    /// Devuelve true si la inyección entró completa: si fallara, el caller NO debe tragar el Win-up
    /// físico (si no, la tecla Win queda "pegada" en Windows — el bug que tuvo el intento anterior).
    /// </summary>
    public static bool SendMaskedWinUp(ushort winVk)
    {
        var inputs = new[]
        {
            MakeKey(VK_CONTROL, down: true),
            MakeKey(VK_CONTROL, down: false),
            MakeKey(winVk,      down: false), // el Win-up que repone al que tragó el hook
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>Inyecta un down+up del VK dado, marcado como propio (lo ignora el hook).</summary>
    public static void SendKeyTap(ushort vk)
    {
        var inputs = new[]
        {
            MakeKey(vk, down: true),
            MakeKey(vk, down: false),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Crea un nuevo escritorio virtual con el atajo nativo Win+Ctrl+D (inyectado).
    /// La DLL de este proyecto no exporta CreateDesktop, así que usamos el shortcut del shell.
    /// Windows cambia el foco al desktop nuevo — el caller debe volver al 0 si hace falta.
    /// </summary>
    public static void CreateVirtualDesktop() => SendKeyCombo(VK_LWIN, VK_CONTROL, VK_D);

    /// <summary>
    /// Inyecta un combo: presiona todas las teclas en orden y las suelta en orden inverso.
    /// Marcado como propio (InjectedSignature) → el hook lo deja pasar sin reprocesar.
    /// </summary>
    public static void SendKeyCombo(params ushort[] vks)
    {
        var inputs = new INPUT[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++)
            inputs[i] = MakeKey(vks[i], down: true);
        for (int i = 0; i < vks.Length; i++)
            inputs[vks.Length + i] = MakeKey(vks[vks.Length - 1 - i], down: false);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>true si NumLock está actualmente encendido (bit toggle).</summary>
    public static bool IsNumLockOn() => (GetKeyState(VK_NUMLOCK) & 0x0001) != 0;

    /// <summary>Si NumLock está encendido, lo apaga una vez. Sin polling.</summary>
    public static void EnsureNumLockOff()
    {
        if (IsNumLockOn())
            SendKeyTap(VK_NUMLOCK); // el toggle inyectado pasa el hook y Windows lo procesa
    }

    private static INPUT MakeKey(ushort vk, bool down) => new()
    {
        type = 1, // INPUT_KEYBOARD
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = down ? 0u : KEYEVENTF_KEYUP,
                dwExtraInfo = InjectedSignature,
            }
        }
    };
}

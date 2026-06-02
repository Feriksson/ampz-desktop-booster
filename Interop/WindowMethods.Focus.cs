using System;

namespace AmpzDesktopBooster.Interop;

/// <summary>
/// Restauración de foco para que el desk ACTUAL nunca quede con el foreground HUÉRFANO. Cuando el
/// foco no apunta a ninguna ventana real (al cerrar la última ventana de un desk, o tras un cambio
/// de desktop programático que no asentó el foco), el hook global de teclado deja de recibir teclas
/// — el MISMO mecanismo del bug de z-order. La cura no es re-armar el hook: es devolverle el foco a
/// algo real (una ventana del frente, o el escritorio, como hacían las versiones viejas).
/// </summary>
internal static partial class WindowMethods
{
    /// <summary>
    /// Si el foreground actual NO es una ventana real del desk actual (está huérfano o es nuestro),
    /// lo arregla: enfoca la ventana real de más arriba; si el desk está VACÍO, manda el foco al
    /// ESCRITORIO. No-op si ya hay un foreground válido de otra app (evita robar foco / parpadeo).
    /// </summary>
    public static void RestoreForegroundOrDesktop(string ownProcess)
    {
        // ¿El foco ya está bien? (ventana real, no cloaked → del desk actual, y de OTRA app). No tocar.
        IntPtr fgNow = GetForegroundWindow();
        if (fgNow != IntPtr.Zero && IsRealTopLevel(fgNow) && !IsCloaked(fgNow)
            && !string.Equals(ProcessNameOf(fgNow), ownProcess, StringComparison.OrdinalIgnoreCase))
            return;

        // Ventana real de OTRA app, la de más arriba en z-order del desk ACTUAL (las de otros desks
        // están cloaked). Excluimos lo nuestro, los fantasmas del shell y el "Program Manager".
        IntPtr top = FindVisible(h =>
            IsRealTopLevel(h) && !IsCloaked(h) && !IsIconic(h) // !IsIconic: no "despertar" una minimizada
            && !string.Equals(ProcessNameOf(h), ownProcess, StringComparison.OrdinalIgnoreCase)
            && TextOf(h) is not "" and not "Program Manager");

        if (top != IntPtr.Zero)
        {
            ForceForeground(top, preserveMaximized: true);
            return;
        }

        // Desk VACÍO → foco al ESCRITORIO (como las versiones viejas). OJO: el SetForegroundWindow al
        // shell NO siempre prende (el foreground puede quedar en 0) — Windows es caprichoso con darle
        // foco al escritorio por API. No importa: la RED real es el ReinstallHook que el caller
        // dispara DESPUÉS, ya con el cierre asentado; un hook recién registrado restaura la entrega
        // aunque el foco haya quedado huérfano. (Reinstalar ANTES de que el cierre se asiente NO
        // alcanza — fue el primer intento fallido.)
        IntPtr shell = GetShellWindow();
        if (shell != IntPtr.Zero)
            FocusToWindowRaw(shell);
    }

    /// <summary>
    /// Da foreground a una ventana SIN tocarle tamaño ni z-order (para el escritorio: no queremos
    /// restaurarlo ni traerlo al frente, sólo que reciba el foco). Mismo truco AttachThreadInput que
    /// <see cref="ForceForeground"/>, porque tras cerrar nuestra ventana podemos NO ser el foreground.
    /// </summary>
    private static void FocusToWindowRaw(IntPtr hwnd)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0u : GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != thisThread
            && AttachThreadInput(thisThread, fgThread, true);
        try { SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(thisThread, fgThread, false); }
    }
}

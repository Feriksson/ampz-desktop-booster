using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// Copia al portapapeles DEL SISTEMA para el gestor de credenciales: el de siempre, sin vueltas.
///
/// Se probó una copia "segura" (fuera del historial de Win+V y borrada a los 20s, como KeePass) y el
/// usuario la RECHAZÓ: es su máquina, y quiere poder pegar una contraseña cuando quiera, también desde
/// el historial. No la reintroduzcas sin que la pida.
///
/// Lo único que queda es el reintento: otra app puede tener el portapapeles abierto (un gestor de
/// historial, una VM, RDP) y entonces OpenClipboard falla con COMException; suele soltarse en
/// milisegundos. Nunca tira: devuelve false y el caller NO dice "copiado".
/// </summary>
public static class ClipboardCopy
{
    private const int Attempts = 5;
    private const int RetryDelayMs = 30;

    /// <summary>Copia <paramref name="text"/>. Llamar desde el thread de UI.</summary>
    public static bool TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (int i = 0; i < Attempts; i++)
        {
            // copy: true = OleFlushClipboard. El dato queda aunque la ventana que lo copió se cierre al
            // instante (el gestor es efímero y se cierra al perder el foco).
            try { Clipboard.SetDataObject(text, copy: true); return true; }
            catch (COMException) { Thread.Sleep(RetryDelayMs); } // tomado por otra app → reintento
            catch { return false; }                               // cualquier otra cosa: no insistimos
        }
        return false;
    }
}

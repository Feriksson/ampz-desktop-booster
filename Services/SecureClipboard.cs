using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// Copia al portapapeles DEL SISTEMA para el gestor de credenciales. Es el portapapeles de siempre
/// (pegás con Ctrl+V donde quieras), con dos cuidados cuando lo copiado es un SECRETO:
///
///   1. Le pide a Windows que NO lo guarde. Sin esto, cada contraseña copiada quedaba para siempre
///      en el historial de Win+V (y, con la sincronización activada, en la nube de Microsoft). Son
///      los formatos registrados que documenta Microsoft para eso, y los mismos que usan los gestores
///      de contraseñas serios:
///        · <c>ExcludeClipboardContentFromMonitorProcessing</c> — su sola presencia (con cualquier
///          dato) pide que los monitores de portapapeles (historial, otras apps) lo ignoren.
///        · <c>CanIncludeInClipboardHistory</c> = DWORD 0 — fuera del historial de Win+V.
///        · <c>CanUploadToCloudClipboard</c> = DWORD 0 — fuera del portapapeles en la nube.
///   2. Lo BORRA solo a los <see cref="SecretLifetime"/>, pero SÓLO si el portapapeles todavía tiene
///      exactamente lo que pusimos: si en el medio copiaste otra cosa, eso es tuyo y no se toca.
///
/// Lo que NO es secreto (usuario, URL, un campo visible) se copia normal: entra al historial como
/// cualquier texto y no se borra — vaciarte el portapapeles por copiar un usuario sería un estorbo.
///
/// Nada de acá registra, avisa ni tira con el valor adentro: un log o un mensaje de error son el
/// lugar más fácil por donde se filtra un secreto. Todo fallo se traga y se informa con un bool.
/// </summary>
public static class SecureClipboard
{
    /// <summary>
    /// Cuánto vive un secreto en el portapapeles. 20s alcanzan para pegarlo en el login que tenés
    /// abierto (que es para lo que se copia) y no tanto como para que quede ahí hasta que pegues
    /// otra cosa sin querer en un chat o un ticket. Es el orden que usan KeePass/Bitwarden.
    /// </summary>
    public static readonly TimeSpan SecretLifetime = TimeSpan.FromSeconds(20);

    private const string FormatExcludeMonitor = "ExcludeClipboardContentFromMonitorProcessing";
    private const string FormatHistory = "CanIncludeInClipboardHistory";
    private const string FormatCloud = "CanUploadToCloudClipboard";

    // Reintentos ante el portapapeles TOMADO: otra app lo tiene abierto (un gestor de historial, una
    // VM, RDP) y OpenClipboard falla con COMException. Suele soltarse en milisegundos; no vale la
    // pena un timer para esto, y la acción la dispara una tecla, así que el bloqueo es imperceptible.
    private const int Attempts = 5;
    private const int RetryDelayMs = 30;

    // Borrado pendiente. UNO solo: una copia nueva reemplaza al anterior (su secreto ya no está en
    // el portapapeles — lo pisó la copia nueva — y su timer sólo podría borrar algo que no es suyo).
    private static DispatcherTimer? _clearTimer;

    // Se guarda la HUELLA de lo copiado y no el texto: para saber "¿sigue siendo lo mío?" alcanza con
    // comparar hashes, y así el secreto no queda vivo en un campo estático durante 20s por nuestra cuenta.
    private static byte[]? _pendingHash;

    /// <summary>
    /// Copia <paramref name="text"/>. Con <paramref name="secret"/> lo excluye del historial/nube y
    /// agenda su borrado. Devuelve false si el portapapeles no se pudo tomar (el caller NO debe decir
    /// "copiado" en ese caso). Llamar desde el thread de UI (lo usan ventanas WPF).
    /// </summary>
    public static bool CopySecret(string text, bool secret)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        if (secret)
        {
            data.SetData(FormatExcludeMonitor, Dword(0));
            data.SetData(FormatHistory, Dword(0));
            data.SetData(FormatCloud, Dword(0));
        }

        // copy: true = OleFlushClipboard. El dato queda en el portapapeles aunque la ventana que lo
        // copió se cierre al instante (las de credenciales son efímeras y se cierran al perder el foco).
        if (!TryClipboard(() => Clipboard.SetDataObject(data, copy: true))) return false;

        CancelPendingClear();
        if (secret) ScheduleClear(text);
        return true;
    }

    /// <summary>DWORD little-endian en un stream: WPF lo pasa como HGLOBAL, que es lo que esperan esos formatos.</summary>
    private static MemoryStream Dword(int value) => new(BitConverter.GetBytes(value));

    private static void ScheduleClear(string text)
    {
        _pendingHash = Hash(text);
        _clearTimer = new DispatcherTimer { Interval = SecretLifetime };
        _clearTimer.Tick += (_, _) =>
        {
            byte[]? mine = _pendingHash;
            CancelPendingClear();
            if (mine is null) return;

            // Sólo si sigue siendo LO NUESTRO. Si copiaste otra cosa en el medio, eso no se toca:
            // borrarte el portapapeles por una copia vieja sería perder lo que acabás de copiar.
            string current = "";
            if (!TryClipboard(() => current = Clipboard.ContainsText() ? Clipboard.GetText() : "")) return;
            if (current.Length == 0 || !CryptographicOperations.FixedTimeEquals(Hash(current), mine)) return;

            TryClipboard(Clipboard.Clear);
        };
        _clearTimer.Start();
    }

    private static void CancelPendingClear()
    {
        _clearTimer?.Stop();
        _clearTimer = null;
        if (_pendingHash is not null) Array.Clear(_pendingHash);
        _pendingHash = null;
    }

    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    /// <summary>Corre una operación de portapapeles con reintentos cortos. Nunca tira.</summary>
    private static bool TryClipboard(Action op)
    {
        for (int i = 0; i < Attempts; i++)
        {
            try { op(); return true; }
            catch (COMException) { Thread.Sleep(RetryDelayMs); } // tomado por otra app → reintento
            catch { return false; }                               // cualquier otra cosa: no insistimos
        }
        return false;
    }
}

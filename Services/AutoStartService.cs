using Microsoft.Win32;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// "Iniciar con Windows" vía la clave Run del usuario actual
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run). Por usuario, SIN admin, y arranca
/// recién cuando el shell ya está listo — clave para esta app: la AppBar necesita el explorer
/// vivo, así que la clave Run (no un servicio que arranca antes) es la opción correcta.
///
/// Apunta SIEMPRE al exe que está corriendo (Environment.ProcessPath): si mañana publicás a una
/// ruta estable y prendés el toggle desde ahí, la entrada se reescribe sola al path nuevo.
/// </summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AmpzDesktopBooster";

    /// <summary>true si la clave Run tiene una entrada para esta app.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string v && !string.IsNullOrWhiteSpace(v);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Prende/apaga el auto-arranque. Idempotente; nunca tira (degrada en silencio).</summary>
    public static void Set(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;   // sin path no escribimos una entrada rota
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                key?.SetValue(ValueName, $"\"{exe}\"");  // comillas: el path puede tener espacios
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // permisos / política de grupo → no rompemos la app por esto
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// "Iniciar con Windows" vía una TAREA PROGRAMADA (\AmpzDesktopBooster), NO la clave Run del
/// usuario. Migrado a propósito: la app necesita levantar lo antes posible (para que la AppBar
/// gane el pixel apenas hay escritorio) y, sobre todo, ELEVADA — el gobierno de ventanas y los
/// hooks globales se comportan mejor sin UAC de por medio en cada reinicio, y una tarea con
/// LogonType=Interactive + RunLevel=Highest arranca elevada SIN prompt (a diferencia de un acceso
/// directo en Startup, que sólo puede levantar sin privilegios).
///
/// La tarea la crea/borra `schtasks.exe` (sin dependencia nueva). Settings NO negociables (ver
/// CLAUDE.md del repo): ExecutionTimeLimit=PT0S (el default de 72h MATARÍA la app), Priority=4 (no
/// el 7 "below normal" que castigaría a una app de hotkeys), MultipleInstances=IgnoreNew, y las dos
/// flags de batería en false porque esto es una app de escritorio, no una tarea de mantenimiento.
/// </summary>
public static class AutoStartService
{
    private const string TaskName = "AmpzDesktopBooster";

    // Claves legacy de la clave Run: se limpian una vez al migrar (ver <see cref="CleanupLegacyRunKey"/>).
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string LegacyValueName = "AmpzDesktopBooster";

    /// <summary>true si la tarea programada existe (`schtasks /Query` sale con código 0).</summary>
    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Prende/apaga el auto-arranque. Idempotente; nunca tira (degrada en silencio). Devuelve si el
    /// estado final QUEDÓ como se pidió — el toggle de la config lo usa para no mentirle al usuario
    /// si canceló el UAC o si schtasks falló por cualquier otra razón.
    /// </summary>
    public static bool Set(bool enabled)
    {
        // Limpieza de UNA sola vez del mecanismo viejo: si conviven la Run key y la tarea, la app
        // arrancaría DOS veces al loguear (una sin privilegios desde Run, otra elevada desde la
        // tarea) y pelearían por el mutex de instancia única. Corre siempre, prenda o apague, porque
        // un usuario que viene de una versión vieja puede tocar el toggle en cualquier sentido.
        CleanupLegacyRunKey();

        try
        {
            bool ok = enabled ? CreateTask() : DeleteTask();
            if (!ok) return IsEnabled() == enabled; // por si el exit code mintió, re-chequeamos

            return IsEnabled() == enabled;
        }
        catch
        {
            return IsEnabled() == enabled;
        }
    }

    /// <summary>
    /// Genera el XML de la tarea (schema 1.4) y la registra con `schtasks /Create ... /XML ... /F`.
    /// Crear una tarea con RunLevel=HighestAvailable requiere admin: si el proceso actual NO está
    /// elevado, se relanza schtasks vía ShellExecute+runas (UN prompt de UAC, sólo al tocar el
    /// toggle) — si el usuario cancela el UAC, Win32Exception 1223 se traga en silencio (queda como
    /// estaba, el caller re-chequea con IsEnabled()).
    /// </summary>
    private static bool CreateTask()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false; // sin path no registramos una tarea rota
        string exeDir = Path.GetDirectoryName(exe) ?? "";

        string tempXml = Path.Combine(Path.GetTempPath(), $"AmpzDesktopBooster_{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempXml, BuildTaskXml(exe, exeDir), Encoding.Unicode); // UTF-16, como pide schtasks /XML
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tempXml}\" /F");
        }
        finally
        {
            try { File.Delete(tempXml); } catch { /* archivo temporal, no crítico */ }
        }
    }

    private static bool DeleteTask() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F");

    /// <summary>
    /// Corre schtasks con los argumentos dados. Si ya estamos elevados, directo y oculto (sin
    /// ventana de consola). Si NO, vía runas — Windows mete el UAC; sin ventana visible después
    /// (WindowStyle Hidden), aunque el prompt de consentimiento en sí no se puede ocultar (es del SO).
    /// </summary>
    private static bool RunSchtasks(string arguments)
    {
        if (ElevationHelper.IsElevated())
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }

        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return false; } // 60s: dar tiempo a que el usuario responda el UAC
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: el usuario cerró/canceló el prompt de UAC. No es un error de la app —
            // simplemente decidió no autorizar. Queda como estaba antes del toggle.
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// XML de la tarea, schema 1.4, EXACTAMENTE con los settings acordados (ver el summary de la
    /// clase). UserId = DOMAIN\user del usuario actual, tanto en el trigger como en el principal —
    /// LogonTrigger sin UserId dispararía para CUALQUIER logon, no sólo el nuestro.
    /// </summary>
    private static string BuildTaskXml(string exe, string exeDir)
    {
        string userId = $@"{Environment.UserDomainName}\{Environment.UserName}";

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Arranca Ampz Desktop Booster al iniciar sesión, elevado, sin prompt de UAC.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{userId}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{userId}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <WorkingDirectory>{exeDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>
    /// Borra la entrada vieja de la clave Run (y su rastro en StartupApproved\Run) si todavía
    /// existe. Corre en HKCU, sin admin. Nunca deben convivir DOS mecanismos de autoarranque — ver
    /// el comentario de <see cref="Set"/>.
    /// </summary>
    private static void CleanupLegacyRunKey()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            runKey?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch { /* permisos/política → no es crítico, la tarea programada manda igual */ }

        try
        {
            using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
            approvedKey?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch { /* idem */ }
    }
}

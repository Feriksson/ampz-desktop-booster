using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;
using AmpzDesktopBooster.Apps;

namespace AmpzDesktopBooster.Services.Browser;

/// <summary>
/// El "browser shim": la app se registra como navegador candidato del SO y, cuando recibe una URL,
/// la reenvía al navegador REAL con --new-window → la ventana nace en el escritorio virtual ACTUAL,
/// en vez de reusar la ventana existente del navegador (que vive en otro desk y te catapulta ahí).
/// Cierra la "Fase 5" pendiente del legacy AHK (ver <see cref="Desktops.PathOpener"/>).
///
/// LÍMITE DURO de Windows 10/11 (anti-hijacking desde 1709): NO se puede setear el navegador por
/// defecto programáticamente — el UserChoice lleva un hash firmado por el SO; si lo escribís a mano,
/// Windows lo resetea. Por eso esta clase SOLO registra a la app como CANDIDATO (aparece en
/// Configuración → Apps predeterminadas); el usuario la elige a mano UNA vez. Después de eso, cada
/// link del sistema lanza nuestro .exe con la URL como argumento.
///
/// Todo el registro vive en HKCU → NO requiere admin.
/// </summary>
public static class BrowserShim
{
    /// <summary>ProgId propio para http/https. Es el valor que Windows guarda en UserChoice al elegirnos.</summary>
    public const string ProgId = "AmpzHTML";

    /// <summary>Nombre con el que figuramos en RegisteredApplications y en StartMenuInternet.</summary>
    public const string ClientName = "AmpzDesktopBooster";

    private const string DisplayName = "Ampz Desktop Booster";

    // ── Detección del navegador real ─────────────────────────────────────────────

    /// <summary>
    /// Resuelve el navegador real al que reenviar. Prioriza el path <paramref name="configured"/> si
    /// existe en disco; si no, autodetecta Brave (el del usuario) en las ubicaciones típicas y, por
    /// último, en el PATH. Devuelve null si no encuentra ninguno.
    /// </summary>
    public static string? ResolveBrowserPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && System.IO.File.Exists(configured))
            return configured;

        return AppDetector.FirstExisting(
                   @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe",
                   @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\Application\brave.exe",
                   @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe")
               ?? AppDetector.InPath("brave.exe");
    }

    // ── Reenvío de la URL ────────────────────────────────────────────────────────

    /// <summary>
    /// Abre la URL en una VENTANA NUEVA del navegador real, en el escritorio virtual actual.
    ///
    /// Replica la limpieza de env de <see cref="AppCatalog"/> (LaunchApp.cs): Brave es Chromium, así
    /// que si esta app fue spawneada bajo VS Code hereda VSCODE_*/ELECTRON_* que matan a Chromium
    /// (ELECTRON_RUN_AS_NODE lo vuelve un Node sin UI → exit code 9, sin ventana). Las purgamos y
    /// paramos el WorkingDirectory en el home del user, replicando el env "limpio" del Start Menu.
    /// </summary>
    public static void OpenInBrave(string url, string? configuredBrowserPath = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        string? browser = ResolveBrowserPath(configuredBrowserPath);
        try
        {
            if (browser is null)
            {
                // No hay navegador real detectable. Último recurso: el handler default del SO. OJO: si
                // ESTA app es el default elegido, esto recursaría — pero el caso "default = nosotros y
                // sin navegador real instalado" es prácticamente imposible (no podrías navegar nada).
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }

            var psi = new ProcessStartInfo(browser)
            {
                Arguments = $"--new-window \"{url}\"",
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            foreach (var k in psi.Environment.Keys.ToList())
            {
                if (k.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase) ||
                    k.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase))
                {
                    psi.Environment.Remove(k);
                }
            }
            Process.Start(psi);
        }
        catch { /* si el spawn falla, no hay nada útil que hacer — no volteamos la app por un link */ }
    }

    // ── Registro / desregistro como navegador candidato ──────────────────────────

    /// <summary>
    /// Escribe en HKCU las claves que hacen aparecer a la app como navegador candidato en
    /// Configuración → Apps predeterminadas. NO cambia el default (Windows no lo permite por código);
    /// el usuario debe elegirla a mano. Idempotente: re-registrar solo reescribe los mismos valores.
    /// </summary>
    public static bool Register()
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe == "") return false;

        try
        {
            // 1) ProgId propio: cómo se abre una URL asociada a AmpzHTML → nuestro exe con la URL.
            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue("", $"{DisplayName} URL");
                using var cmd = prog.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", $"\"{exe}\" \"%1\"");
            }

            // 2) Cliente de navegador + Capabilities (lo que Windows lista en "Apps predeterminadas").
            string clientRoot = $@"Software\Clients\StartMenuInternet\{ClientName}";
            using (var client = Registry.CurrentUser.CreateSubKey(clientRoot))
            {
                client.SetValue("", DisplayName);

                using (var cmd = client.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue("", $"\"{exe}\"");

                using (var caps = client.CreateSubKey("Capabilities"))
                {
                    caps.SetValue("ApplicationName", DisplayName);
                    caps.SetValue("ApplicationDescription",
                        "Abre los links en el escritorio virtual actual (shim de navegador).");
                    caps.SetValue("ApplicationIcon", $"{exe},0");

                    using var urls = caps.CreateSubKey("URLAssociations");
                    urls.SetValue("http", ProgId);
                    urls.SetValue("https", ProgId);
                }
            }

            // 3) Darse de alta en RegisteredApplications → apunta a las Capabilities de arriba.
            using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                reg.SetValue(ClientName, $@"{clientRoot}\Capabilities");

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Borra las claves del registro. Tras esto Windows deja de ofrecernos como navegador; si
    /// estábamos elegidos como default, el SO vuelve a pedir uno y el usuario re-elige el suyo.
    /// </summary>
    public static bool Unregister()
    {
        try
        {
            using (var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
                reg?.DeleteValue(ClientName, throwOnMissingValue: false);

            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Clients\StartMenuInternet\{ClientName}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>true si nuestras claves de registro están presentes (registrados como candidato).</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications");
            return reg?.GetValue(ClientName) is not null;
        }
        catch { return false; }
    }

    /// <summary>
    /// true si la app es AHORA MISMO el navegador elegido por el usuario para https (UserChoice). Es
    /// el estado REAL que pinta la UI — distinto de estar solo registrado como candidato.
    /// </summary>
    public static bool IsDefault()
    {
        try
        {
            using var uc = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            return (uc?.GetValue("ProgId") as string) == ProgId;
        }
        catch { return false; }
    }

    /// <summary>Abre Configuración de Windows en "Apps predeterminadas" para que el usuario nos elija.</summary>
    public static void OpenWindowsDefaultApps()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
        catch { /* sin Settings (raro) → el usuario lo abre a mano */ }
    }
}

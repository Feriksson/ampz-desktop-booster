using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace AmpzDesktopBooster.Apps;

/// <summary>Una app disponible para "Abrir con": su nombre visible y cómo abre los targets.</summary>
public sealed record LaunchApp(string Name, Action<IReadOnlyList<string>> Launch);

/// <summary>
/// Construye la lista de apps DISPONIBLES para Abrir con: las built-in que estén instaladas
/// (auto-detectadas) + las que el usuario haya definido. Nada hardcodeado que no se verifique.
/// </summary>
public static class AppCatalog
{
    public static IReadOnlyList<LaunchApp> GetAvailable(AppsConfig userApps)
    {
        var list = new List<LaunchApp>();

        // ── VS Code ──
        var code = AppDetector.FirstExisting(
                       @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe",
                       @"%ProgramFiles%\Microsoft VS Code\Code.exe")
                   ?? AppDetector.InPath("code.cmd");
        if (code is not null)
            list.Add(new("Visual Studio Code", paths =>
            {
                // VSCode abre múltiples carpetas en una sola ventana.
                var args = string.Join(" ", paths.Select(p => Quote(p)));
                Start(code, args);
            }));

        // ── Claude CLI (shell preferido directo, NO wt) ──
        // Por qué directo: wt.exe destroza las comillas anidadas del comando y, por su
        // single-instance, puede pegar la pestaña a una ventana wt admin ya abierta (terminaba
        // elevado). El shell directo (pwsh → powershell) hereda nuestro token (no-admin).
        var claude = AppDetector.InPath("claude.exe")
                     ?? AppDetector.InPath("claude.cmd")
                     ?? AppDetector.FirstExisting(@"%USERPROFILE%\.local\bin\claude.exe");
        if (claude is not null)
            list.Add(new("Claude CLI", paths => ForEach(paths, p =>
                Shell.RunInDir(p, $"& '{claude}' --permission-mode bypassPermissions"))));

        // ── OpenCode CLI (shell preferido directo) ──
        var opencode = AppDetector.InPath("opencode.exe") ?? AppDetector.InPath("opencode.cmd");
        if (opencode is not null)
            list.Add(new("OpenCode CLI", paths => ForEach(paths, p =>
                Shell.RunInDir(p, $"& '{opencode}'"))));

        // ── Warp ──
        var warp = AppDetector.FirstExisting(
                       @"%LOCALAPPDATA%\Programs\Warp\warp.exe",
                       @"%ProgramFiles%\Warp\warp.exe")
                   ?? AppDetector.InPath("warp.exe");
        if (warp is not null)
            list.Add(new("Warp", paths => ForEach(paths, p =>
                Process.Start(new ProcessStartInfo(warp) { UseShellExecute = true, WorkingDirectory = p }))));

        // ── WSL (wsl.exe directo en el directorio) ──
        var wsl = AppDetector.InPath("wsl.exe");
        if (wsl is not null)
            list.Add(new("WSL", paths => ForEach(paths, p =>
                Process.Start(new ProcessStartInfo(wsl) { UseShellExecute = true, WorkingDirectory = p }))));

        // ── Apps del usuario (pestaña Aplicaciones) ──
        foreach (var u in userApps.Apps)
        {
            if (string.IsNullOrWhiteSpace(u.ExePath) || string.IsNullOrWhiteSpace(u.Name))
                continue;
            var app = u; // captura para el closure
            list.Add(new(app.Name, paths => ForEach(paths, p =>
            {
                string args = app.Args.Contains("{path}") ? app.Args.Replace("{path}", p) : Quote(p);
                Start(app.ExePath, args);
            })));
        }

        return list;
    }

    private static string Quote(string s) => $"\"{s}\"";

    private static void ForEach(IReadOnlyList<string> paths, Action<string> action)
    {
        foreach (var p in paths) action(p);
    }

    private static void Start(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = args });
}

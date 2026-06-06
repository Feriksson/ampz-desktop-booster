using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AmpzDesktopBooster.Interop;

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
                // BUG cazado con instrumentación a archivo (ampz-abrircon.log, 2026-06-06):
                //
                // Si AmpzDesktopBooster es spawneado desde un proceso hijo de VS Code (el shell
                // del extension Claude Code, o cualquier terminal integrado), HEREDA env vars
                // que VS Code inyecta en sus child processes. Las críticas:
                //   - ELECTRON_RUN_AS_NODE=1       → vuelve a Code.exe un intérprete Node SIN UI
                //   - VSCODE_IPC_HOOK=\\.\pipe\... → apunta al pipe interno del VS Code padre
                //   - VSCODE_PID, VSCODE_NLS_CONFIG, VSCODE_ESM_ENTRYPOINT, etc.
                //
                // Cuando spawneamos Code.exe con ese env contaminado, Code.exe arranca en modo
                // Node, NO crea ventana, exit code 9, sin rastro en Task Manager (al menos sin
                // ventana visible). Manual desde cmd y desde el shortcut del Start Menu funciona
                // porque su env está limpia (parent = explorer.exe).
                //
                // Fix: limpiar TODAS las VSCODE_* y ELECTRON_* del env del child antes del spawn,
                // replicando la env "limpia" del shortcut. Sumamos -n para forzar ventana nueva
                // y WorkingDirectory en el home del user (no en bin\Debug donde vive este exe).
                var args = "-n " + string.Join(" ", paths.Select(p => Quote(p)));
                var psi = new System.Diagnostics.ProcessStartInfo(code)
                {
                    Arguments = args,
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

                // Snapshot de HWNDs antes del spawn → el watchdog maximiza la(s) NUEVA(s) que
                // aparezcan. VS Code no tiene flag --maximize en CLI; la única vía limpia es
                // dispararle SW_SHOWMAXIMIZED desde afuera apenas se renderiza la ventana.
                var beforeHwnds = new HashSet<IntPtr>(WindowMethods.VisibleTopLevelOf("Code.exe"));
                System.Diagnostics.Process.Start(psi);
                MaximizeNewCodeWindows(beforeHwnds);
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

    /// <summary>
    /// Polea por hasta ~5s buscando HWNDs de Code.exe que NO estuvieran antes del spawn — los
    /// maximiza apenas aparecen y corta. Necesario porque VS Code no tiene flag --maximize: el
    /// `window.newWindowDimensions` del settings.json puede estar en "default" o "inherit", y
    /// queremos el comportamiento independientemente del setting del user. El polling vive en
    /// thread del pool (no UI), ShowWindow se llama vía P/Invoke así que no requiere Dispatcher.
    /// </summary>
    private static void MaximizeNewCodeWindows(HashSet<IntPtr> before)
    {
        System.Threading.Tasks.Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await System.Threading.Tasks.Task.Delay(200);
                var current = WindowMethods.VisibleTopLevelOf("Code.exe");
                var newOnes = current.Where(h => !before.Contains(h)).ToList();
                if (newOnes.Count > 0)
                {
                    foreach (var h in newOnes) WindowMethods.Maximize(h);
                    return;
                }
            }
        });
    }

    private static string Quote(string s) => $"\"{s}\"";

    private static void ForEach(IReadOnlyList<string> paths, Action<string> action)
    {
        foreach (var p in paths) action(p);
    }

    private static void Start(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = args });
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Apps;

/// <summary>
/// Un comando a correr en un directorio: lo que ocupa UNA pestaña de terminal. Existe para que
/// <see cref="Shell.RunMany"/> reciba el lote ya resuelto y no tenga que saber nada de servicios —
/// Shell no conoce la capa de dominio, igual que SystemMonitor no conoce WPF.
/// </summary>
public readonly record struct ShellJob(string WorkingDir, string Command);

/// <summary>
/// Resuelve y lanza el shell preferido. "PowerShell" (el moderno, v7+) es pwsh.exe; "Windows
/// PowerShell" (el viejo, v5.1) es powershell.exe. Preferimos pwsh.exe y caemos a powershell.exe
/// sólo si pwsh no está instalado — así en una máquina con PowerShell 7 se usa ése, y la app
/// sigue funcionando (compartible) donde sólo hay Windows PowerShell.
///
/// VENTANEO POR ESCRITORIO (la decisión la toma la APP, no Windows Terminal):
/// En Win11, aunque lances el .exe directo, si WT es el "terminal por defecto" él intercepta la
/// consola y decide la ventana según su `windowingBehavior`. Con `useExisting`, la pestaña se mete
/// en una ventana de WT que ya existe — y como esta app maneja los escritorios virtuales con
/// VirtualDesktopAccessor.dll (un tercero), la detección de VD de WT NO se alinea: la pestaña
/// puede terminar en una ventana de OTRO escritorio y desde el actual "no aparece nada".
///
/// Por eso lanzamos vía `wt.exe` con `-w` EXPLÍCITO: `-w` pisa `windowingBehavior`, así que NO
/// dependemos de la (poco confiable) detección de escritorios de WT. La app pregunta — con su
/// propia DLL — si hay una ventana de WT en el escritorio ACTUAL: si la hay, la trae al frente y
/// abre una pestaña ahí (`-w last`); si no, fuerza una ventana NUEVA acá (`-w new`).
///
/// El comando anidado (ej. claude CLI) se pasa con `-EncodedCommand` (base64 UTF-16LE), NO con
/// `-Command "..."`: el base64 es opaco al parser de comillas/`;` de wt.exe, que era justamente
/// lo que rompía los comandos anidados (el motivo por el que antes evitábamos wt). Si WT no está
/// instalado, caemos al lanzamiento directo del .exe (en esa máquina el host es conhost, que igual
/// abre ventana nueva en el escritorio actual) — la app sigue siendo compartible.
/// </summary>
public static class Shell
{
    /// <summary>Clase Win32 de las ventanas de Windows Terminal.</summary>
    private const string WtWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";

    /// <summary>
    /// Inyectado en el arranque (<c>App.OnStartup</c>). Lo usamos para resolver el escritorio
    /// virtual actual sin tocar P/Invoke de desktops directo (DesktopService es la capa alta).
    /// Puede ser null muy temprano o en tests → caemos a "siempre ventana nueva".
    /// </summary>
    public static DesktopService? Desktops { get; set; }

    /// <summary>
    /// Shell preferido para cuando hay que correr un COMANDO (claude/opencode CLI). Resuelve un
    /// pwsh **LANZABLE**, NO el .exe del paquete MSIX (`C:\Program Files\WindowsApps\...\pwsh.exe`):
    /// ese path, lanzado por CreateProcess, falla al cargar hostfxr.dll (el contenedor de AppX lo
    /// bloquea). El **alias de ejecución** (`%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe`) sí se
    /// lanza (dispara la activación AppX). Orden: alias Store → instalación MSI → Windows PowerShell
    /// 5.1 (siempre presente). Por eso NO usamos <c>AppDetector.InPath("pwsh.exe")</c>: el PATH
    /// suele devolver primero el path del paquete (roto).
    /// </summary>
    public static string PreferredExe =>
        AppDetector.FirstExisting(
            @"%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe", // alias de ejecución del Store (MSIX)
            @"%ProgramFiles%\PowerShell\7\pwsh.exe")          // instalación MSI clásica
        ?? "powershell.exe";                                   // Windows PowerShell 5.1 — siempre está

    /// <summary>Abre una ventana del shell en <paramref name="workingDir"/> (sin comando extra).</summary>
    public static void OpenInDir(string workingDir) => Launch(workingDir, command: null);

    /// <summary>Abre el shell en <paramref name="workingDir"/> y corre <paramref name="command"/>.</summary>
    public static void RunInDir(string workingDir, string command) => Launch(workingDir, command);

    /// <summary>
    /// Corre VARIOS comandos en UNA sola ventana de terminal, uno por pestaña.
    ///
    /// Existe porque llamar N veces a <see cref="RunInDir"/> en un lazo NO da N pestañas sino N
    /// VENTANAS, y el motivo está en <see cref="ResolveWindowTarget"/>: la decisión "ventana nueva vs
    /// pestaña" se toma mirando si YA hay una ventana de WT en este escritorio, pero wt.exe las crea
    /// de forma ASÍNCRONA (vía su monarca). En un lazo sincrónico las N preguntas se contestan antes
    /// de que la primera ventana exista → las N piden ventana nueva. Lanzando a mano no se ve, porque
    /// entre clic y clic la ventana alcanza a nacer.
    ///
    /// La cura es que deje de haber N decisiones: se arma UN comando de wt con la primera pestaña y
    /// un <c>; new-tab</c> por cada una de las siguientes. Un solo proceso, una sola decisión, cero
    /// carrera. Arreglarlo esperando entre lanzadas habría sido tapar una carrera con timing — en
    /// este repo eso ya falló antes (ver el bloque del z-order en el CLAUDE.md).
    ///
    /// ⚠ El <c>;</c> separador va como argumento PROPIO y sin comillas, que es como wt.exe lo
    /// reconoce. Lo único que viaja crudo por su parser es el DIRECTORIO (el comando del usuario va en
    /// base64 y es opaco, ver <see cref="Launch"/>): un workingDir con un <c>;</c> adentro partiría el
    /// comando. Es un path que en la práctica no existe, y el precio de blindarlo sería escapar a mano
    /// la línea de wt — justo lo que el base64 vino a evitar.
    /// </summary>
    public static void RunMany(IReadOnlyList<ShellJob> jobs)
    {
        if (jobs.Count == 0) return;

        // Uno solo no necesita nada de esto: se va por el camino de siempre, que además resuelve el
        // foco y el fallback sin WT sin duplicar una línea.
        if (jobs.Count == 1)
        {
            Launch(jobs[0].WorkingDir, jobs[0].Command);
            return;
        }

        // Sin Windows Terminal no hay pestañas que valgan: conhost abre una ventana por comando y no
        // hay forma de agruparlas. Se degrada al comportamiento viejo en vez de no lanzar nada.
        if (AppDetector.InPath("wt.exe") is null)
        {
            foreach (var job in jobs) LaunchDirect(job.WorkingDir, job.Command);
            return;
        }

        string target = ResolveWindowTarget(out int current);

        var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
        ScrubInheritedEditorEnv(psi);
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add(target);

        for (int i = 0; i < jobs.Count; i++)
        {
            // La PRIMERA pestaña es el subcomando implícito de wt; de la segunda en adelante hay que
            // nombrarlo (`new-tab`) detrás del separador.
            if (i > 0)
            {
                psi.ArgumentList.Add(";");
                psi.ArgumentList.Add("new-tab");
            }
            AddTabArgs(psi, jobs[i].WorkingDir, jobs[i].Command);
        }

        Process.Start(psi);

        if (target == "new" && current >= 0)
            WindowFocuser.FocusWhenReady(hwnd => IsTerminalOn(hwnd, current), preserveMaximized: true);
    }

    /// <summary>
    /// Núcleo del lanzamiento: decide reusar/forzar ventana por escritorio y dispara wt.exe.
    /// Si no hay WT, cae al lanzamiento directo del .exe.
    /// </summary>
    private static void Launch(string workingDir, string? command)
    {
        if (AppDetector.InPath("wt.exe") is null)
        {
            LaunchDirect(workingDir, command);
            return;
        }

        string target = ResolveWindowTarget(out int current);

        // UseShellExecute = FALSE a propósito: wt.exe es un App Execution Alias (reparse point en
        // WindowsApps). Por ShellExecuteEx (UseShellExecute=true) el alias NO recibe los argumentos
        // (ArgumentList se pierde) → wt arranca pelado, le habla al monarca y muere sin abrir nada.
        // Por CreateProcess (UseShellExecute=false) el alias se resuelve por PATH y SÍ recibe args.
        var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
        ScrubInheritedEditorEnv(psi);
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add(target);
        AddTabArgs(psi, workingDir, command);
        Process.Start(psi);

        // FOCO GARANTIZADO a la ventana NUEVA. El reuse ("last") ya hizo ForceForeground arriba;
        // el caso pendiente es "new". Aunque sea un Process.Start, la ventana NO siempre nace con
        // foco: si ya hay un proceso "monarca" de WT vivo (aunque sea en OTRO escritorio), la
        // ventana nueva la crea ESE proceso —que no es el foreground—, así que el anti-robo-de-foco
        // de Windows la deja atrás. Encima venimos de un hotkey global (tampoco somos foreground).
        // wt.exe crea la ventana ASÍNCRONO vía el monarca, así que todavía no existe acá: la
        // esperamos (sin bloquear el hilo de UI) y la traemos al frente. Sólo si sabemos el desk.
        // preserveMaximized: no le tocamos el tamaño a WT, sólo le damos foreground/foco. Llegamos
        // acá sólo cuando NO había WT en este desk, así que la primera ventana de WT que aparezca acá
        // es la nuestra. El timer corre en el hilo de UI (el router difiere todo al Dispatcher).
        if (target == "new" && current >= 0)
            WindowFocuser.FocusWhenReady(hwnd => IsTerminalOn(hwnd, current), preserveMaximized: true);
    }

    /// <summary>
    /// Decide el <c>-w</c> de wt.exe: reusar la ventana de WT de ESTE escritorio (<c>last</c>) o
    /// forzar una nueva acá (<c>new</c>). Devuelve además el escritorio actual (-1 si no se pudo
    /// resolver), que el caller necesita para el foco de la ventana nueva.
    ///
    /// ⚠ La respuesta sólo vale para UN lanzamiento: wt.exe crea sus ventanas ASÍNCRONO vía el
    /// monarca, así que preguntar dos veces seguidas devuelve "no hay" las dos veces aunque la
    /// primera ya haya pedido una. Por eso el arranque grupal NO llama acá una vez por servicio —
    /// ver <see cref="RunMany"/>.
    /// </summary>
    private static string ResolveWindowTarget(out int current)
    {
        // "new" por defecto: si no podemos resolver el escritorio (Desktops null) o no hay una
        // ventana de WT acá, abrimos una ventana nueva en el escritorio actual.
        current = Desktops?.Current ?? -1;
        if (current < 0) return "new";

        IntPtr existing = FindTerminalOnDesktop(current);
        if (existing == IntPtr.Zero) return "new";

        // Hay WT en ESTE escritorio → traerla al frente (queda como "last" / MRU de WT) y abrir la
        // pestaña ahí. ForceForeground maneja el robo-de-foco al venir de un hotkey global (la app no
        // es el foreground en ese instante). preserveMaximized: NO le sacamos el maximizado a la
        // ventana de WT (sólo la des-minimizamos si hace falta).
        WindowMethods.ForceForeground(existing, preserveMaximized: true);
        return "last";
    }

    /// <summary>
    /// Argumentos de UNA pestaña: directorio y, si hay comando, el shell host que lo corre. Lo
    /// comparten el lanzamiento simple y el grupal a propósito — si divergieran, un servicio lanzado
    /// solo y el mismo servicio lanzado en lote podrían arrancar distinto, que es el peor tipo de bug
    /// (el que sólo aparece por la puerta que no probaste).
    /// </summary>
    private static void AddTabArgs(ProcessStartInfo psi, string workingDir, string? command)
    {
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(workingDir);

        // Sin comando → NO especificamos shell: WT abre su PERFIL POR DEFECTO (PowerShell), que
        // lanza pwsh por el mecanismo correcto (activación AppX). Pasarle el .exe del paquete por
        // ruta lo rompía (hostfxr). Es, además, como el usuario abre WT a diario → garantizado.
        if (command is null) return;

        // Hay comando (claude/opencode CLI, npm run dev) → necesitamos un shell host. Pasamos el pwsh
        // LANZABLE (alias, no el path del paquete) + -EncodedCommand (base64 UTF-16LE): el base64 es
        // opaco al parser de comillas/`;` de wt.exe que rompía los comandos anidados.
        psi.ArgumentList.Add(PreferredExe);
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
    }

    /// <summary>
    /// ¿Es <paramref name="hwnd"/> una ventana de Windows Terminal en el escritorio virtual dado?
    /// El filtro de "visible" lo aplica el enumerador (<see cref="WindowMethods.FindVisible"/> /
    /// <see cref="WindowFocuser"/>); acá sólo decidimos clase + escritorio. Una única definición del
    /// criterio, reusada tanto para BUSCAR la ventana (reuse) como para ESPERARLA (ventana nueva).
    /// </summary>
    private static bool IsTerminalOn(IntPtr hwnd, int desktop) =>
        WindowMethods.ClassOf(hwnd) == WtWindowClass
        && VirtualDesktopAccessor.GetWindowDesktopNumber(hwnd) == desktop;

    /// <summary>
    /// Primera ventana de Windows Terminal VISIBLE en el escritorio virtual dado (IntPtr.Zero si
    /// ninguna). Mismo patrón "desktop-aware" que <c>QuickActions.OpenDownloads</c>.
    /// </summary>
    private static IntPtr FindTerminalOnDesktop(int desktop) =>
        WindowMethods.FindVisible(hwnd => IsTerminalOn(hwnd, desktop));

    /// <summary>
    /// Saca del entorno del hijo las env vars que VS Code inyecta en TODOS sus descendientes. Si esta
    /// app fue spawneada desde un proceso hijo de VS Code (terminal integrado, agente, F5), las hereda
    /// — y se las pasaría a todo lo que lancemos. La asesina es <c>ELECTRON_RUN_AS_NODE=1</c>, que
    /// convierte cualquier binario Electron en un intérprete Node sin UI (exit code 9, sin ventana, sin
    /// rastro); <c>VSCODE_IPC_HOOK</c> apunta al pipe del VS Code PADRE y rompe el handshake.
    ///
    /// Se aplica acá —y no sólo en "Abrir con"— porque desde que se lanzan SERVICIOS (npm/php/expo) el
    /// shell pasó a ser la puerta por la que sale cualquier cosa: un `npm run dev` que arranque un
    /// Electron, o un script que invoque `code`, moriría igual. Replica el entorno LIMPIO que tendría
    /// el mismo comando lanzado desde el shortcut del Start Menu (parent = explorer.exe).
    /// Ver el bloque de Electron en el CLAUDE.md: el bug se cazó con instrumentación a archivo.
    /// </summary>
    private static void ScrubInheritedEditorEnv(ProcessStartInfo psi)
    {
        foreach (var k in psi.Environment.Keys.ToList())
        {
            if (k.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase))
            {
                psi.Environment.Remove(k);
            }
        }
    }

    /// <summary>
    /// Fallback sin Windows Terminal: lanza el .exe directo. OJO: acá NO podemos limpiar el entorno
    /// heredado como en el camino de wt — <see cref="ProcessStartInfo.Environment"/> sólo se puede
    /// tocar con <c>UseShellExecute=false</c>, y lo necesitamos en true para heredar nuestro token
    /// no-admin. Es un mal menor consciente: este camino sólo corre en máquinas SIN Windows Terminal.
    /// En esa máquina el host es conhost, que abre ventana nueva en el escritorio actual.
    /// </summary>
    private static void LaunchDirect(string workingDir, string? command)
    {
        Process.Start(new ProcessStartInfo(PreferredExe)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDir,
            Arguments = command is null ? "-NoExit" : $"-NoExit -Command \"{command}\"",
        });
    }
}

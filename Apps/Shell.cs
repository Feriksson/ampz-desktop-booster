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
/// <summary>
/// <paramref name="CloseOnExit"/>: si true, la pestaña se arma SIN <c>-NoExit</c> (ver
/// <see cref="Shell.AddTabArgs"/>) para que pwsh salga solo y Windows Terminal cierre la pestaña —
/// pensado para comandos LANZADORES (ej. "code .") que no dejan nada corriendo. Default false:
/// mantiene el comportamiento de siempre para todo lo que no venga de un <c>ServiceEntry</c> con el
/// flag tildado.
/// </summary>
/// <summary>
/// <paramref name="Elevated"/>: si true, la terminal de este comando nace con token de
/// ADMINISTRADOR. Default false — y ese default es el punto: la app corre elevada (tarea
/// programada) y TODO lo que lanza sale de-elevado salvo pedido explícito. Ver
/// <see cref="Shell.StartTerminal"/>.
/// </summary>
public readonly record struct ShellJob(string WorkingDir, string Command, bool CloseOnExit = false,
                                       bool Elevated = false);

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
///
/// ELEVACIÓN: por defecto la terminal nace SIN privilegios aunque la app corra elevada; elevada es
/// opt-in por lanzamiento (<c>elevated</c>). Y las dos familias NO se mezclan: un Windows Terminal
/// elevado y uno normal son instancias separadas (cada una con su monarca), así que la decisión de
/// reusar ventana mira sólo las del MISMO lado — ver <see cref="IsTerminalOn"/>.
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
    public static void OpenInDir(string workingDir) => Launch(workingDir, command: null, closeOnExit: false);

    /// <summary>
    /// Abre el shell en <paramref name="workingDir"/> y corre <paramref name="command"/>.
    /// <paramref name="closeOnExit"/> — ver <see cref="ShellJob.CloseOnExit"/>.
    /// <paramref name="elevated"/> — ver <see cref="ShellJob.Elevated"/>.
    /// </summary>
    public static void RunInDir(string workingDir, string command, bool closeOnExit = false,
                                bool elevated = false) =>
        Launch(workingDir, command, closeOnExit, elevated);

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
    ///
    /// LOTE MIXTO (elevados + normales): se parte en DOS invocaciones, una por lado. No es una
    /// concesión: una ventana de WT tiene UN token, así que esos comandos no pueden compartirla de
    /// ninguna forma. Y partirlo NO reintroduce la carrera de arriba, porque cada mitad decide
    /// mirando sólo las ventanas de SU elevación — son dos preguntas distintas, no la misma dos veces.
    /// </summary>
    public static void RunMany(IReadOnlyList<ShellJob> jobs)
    {
        if (jobs.Count == 0) return;

        var elevatedJobs = jobs.Where(j => j.Elevated).ToList();
        if (elevatedJobs.Count > 0 && elevatedJobs.Count < jobs.Count)
        {
            // Primero la tanda normal y al final la elevada: si la app no corre elevada, la elevada
            // dispara el UAC, y conviene que lo último que pida atención sea eso.
            RunMany(jobs.Where(j => !j.Elevated).ToList());
            RunMany(elevatedJobs);
            return;
        }

        // De acá en más el lote es HOMOGÉNEO: la elevación de cualquiera es la de todos.
        bool elevated = jobs[0].Elevated;

        // Uno solo no necesita nada de esto: se va por el camino de siempre, que además resuelve el
        // foco y el fallback sin WT sin duplicar una línea.
        if (jobs.Count == 1)
        {
            Launch(jobs[0].WorkingDir, jobs[0].Command, jobs[0].CloseOnExit, elevated);
            return;
        }

        // Sin Windows Terminal no hay pestañas que valgan: conhost abre una ventana por comando y no
        // hay forma de agruparlas. Se degrada al comportamiento viejo en vez de no lanzar nada.
        if (AppDetector.InPath("wt.exe") is null)
        {
            foreach (var job in jobs) LaunchDirect(job.WorkingDir, job.Command, job.CloseOnExit, elevated);
            return;
        }

        string target = ResolveWindowTarget(elevated, out int current);

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
            AddTabArgs(psi, jobs[i].WorkingDir, jobs[i].Command, jobs[i].CloseOnExit);
        }

        if (!StartTerminal(psi, elevated)) return;

        if (target == "new" && current >= 0)
            WindowFocuser.FocusWhenReady(hwnd => IsTerminalOn(hwnd, current, elevated), preserveMaximized: true);
    }

    /// <summary>
    /// Núcleo del lanzamiento: decide reusar/forzar ventana por escritorio y dispara wt.exe.
    /// Si no hay WT, cae al lanzamiento directo del .exe.
    /// </summary>
    private static void Launch(string workingDir, string? command, bool closeOnExit = false,
                               bool elevated = false)
    {
        if (AppDetector.InPath("wt.exe") is null)
        {
            LaunchDirect(workingDir, command, closeOnExit, elevated);
            return;
        }

        string target = ResolveWindowTarget(elevated, out int current);

        // UseShellExecute = FALSE a propósito: wt.exe es un App Execution Alias (reparse point en
        // WindowsApps). Por ShellExecuteEx (UseShellExecute=true) el alias NO recibe los argumentos
        // (ArgumentList se pierde) → wt arranca pelado, le habla al monarca y muere sin abrir nada.
        // Por CreateProcess (UseShellExecute=false) el alias se resuelve por PATH y SÍ recibe args.
        var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
        ScrubInheritedEditorEnv(psi);
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add(target);
        AddTabArgs(psi, workingDir, command, closeOnExit);
        if (!StartTerminal(psi, elevated)) return;

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
        // El criterio de espera lleva la MISMA elevación que el de reuso: con un WT del otro lado ya
        // abierto en este desk, "la primera ventana de WT que aparezca" sería esa y no la nuestra.
        if (target == "new" && current >= 0)
            WindowFocuser.FocusWhenReady(hwnd => IsTerminalOn(hwnd, current, elevated), preserveMaximized: true);
    }

    /// <summary>
    /// Dispara el wt.exe ya armado con el token que corresponde. Punto ÚNICO donde se decide la
    /// elevación de una terminal — lo comparten el lanzamiento simple y el grupal. Devuelve false
    /// sólo cuando se pidió elevado y NO se lanzó nada (UAC cancelado, o imposible): el caller no
    /// tiene ventana que esperar.
    ///
    /// NORMAL (el default): <see cref="UnelevatedLauncher"/> CON TRAMPOLÍN. Hallazgo 2026-10-10: con
    /// el lanzamiento directo (CreateProcess + padre adoptado = explorer) el Windows Terminal y sus
    /// pestañas salían ELEVADOS igual — wt.exe es un App Execution Alias y su activación no respetaba
    /// al padre declarado. Con el trampolín el de-elevado es un cmd.exe común y es él quien activa el
    /// alias. Si la app no corre elevada, UnelevatedLauncher es un Process.Start de siempre.
    ///
    /// ELEVADO, app elevada: Process.Start pelado — el hijo hereda nuestro token, que es justo lo
    /// que acá se quiere (y lo que <see cref="UnelevatedLauncher"/> existe para evitar en el resto).
    ///
    /// ELEVADO, app SIN elevar: hace falta el UAC (verbo <c>runas</c>, que exige ShellExecute). Pero
    /// por ShellExecute el alias wt.exe NO recibe sus argumentos (ver <see cref="Launch"/>), así que
    /// lo que se eleva es un cmd.exe —binario real, que sí los recibe— con el mismo trampolín
    /// <c>start "" wt.exe …</c>. Un solo prompt de UAC. Costos asumidos de esta rama: (1) con
    /// ShellExecute no se puede tocar el entorno, así que NO lleva el scrub de VSCODE_*/ELECTRON_*;
    /// (2) si el UAC se resuelve con las credenciales de OTRO usuario, ese cmd puede no tener el
    /// alias wt.exe en su PATH. Lo que NUNCA hace es lanzar sin elevar diciendo que elevó: si no
    /// puede, no lanza y lo anota.
    /// </summary>
    private static bool StartTerminal(ProcessStartInfo psi, bool elevated)
    {
        if (!elevated)
        {
            UnelevatedLauncher.Start(psi, viaTrampoline: true);
            return true;
        }

        try
        {
            if (ElevationHelper.IsElevated())
            {
                Process.Start(psi);
                return true;
            }

            string? cmdArgs = UnelevatedLauncher.TryBuildTrampolineArguments(psi);
            if (cmdArgs is null)
            {
                // Un lote tan grande que no entra en una línea de cmd (8191). Sin elevar no hay otra
                // vía que respete los argumentos del alias → no se lanza a medias.
                UnelevatedLauncher.LogFailure(psi.FileName,
                    "lanzamiento ELEVADO cancelado: la línea no entra en el límite de cmd.exe y la app no corre elevada");
                return false;
            }

            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = cmdArgs,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden, // el cmd es un pasamanos: que no parpadee
            });
            return true;
        }
        catch (Exception ex)
        {
            // Lo normal acá es ERROR_CANCELLED (1223): el usuario dijo que no en el UAC. Es SU
            // decisión — no se reintenta y, sobre todo, no se "arregla" lanzando sin elevar.
            UnelevatedLauncher.LogFailure(psi.FileName,
                $"lanzamiento ELEVADO no realizado: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Decide el <c>-w</c> de wt.exe: reusar la ventana de WT de ESTE escritorio (<c>last</c>) o
    /// forzar una nueva acá (<c>new</c>). Devuelve además el escritorio actual (-1 si no se pudo
    /// resolver), que el caller necesita para el foco de la ventana nueva.
    ///
    /// Sólo cuentan las ventanas cuya elevación coincide con <paramref name="elevated"/>: <c>-w last</c>
    /// lo resuelve el monarca del lado que lanza, y ése no ve las ventanas del otro. Reusar una
    /// ventana "que está ahí" pero del otro lado traía al frente la equivocada y la pestaña
    /// terminaba en la última ventana de NUESTRO lado — posiblemente en otro escritorio.
    ///
    /// ⚠ La respuesta sólo vale para UN lanzamiento: wt.exe crea sus ventanas ASÍNCRONO vía el
    /// monarca, así que preguntar dos veces seguidas devuelve "no hay" las dos veces aunque la
    /// primera ya haya pedido una. Por eso el arranque grupal NO llama acá una vez por servicio —
    /// ver <see cref="RunMany"/>.
    /// </summary>
    private static string ResolveWindowTarget(bool elevated, out int current)
    {
        // "new" por defecto: si no podemos resolver el escritorio (Desktops null) o no hay una
        // ventana de WT acá, abrimos una ventana nueva en el escritorio actual.
        current = Desktops?.Current ?? -1;
        if (current < 0) return "new";

        IntPtr existing = FindTerminalOnDesktop(current, elevated);
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
    private static void AddTabArgs(ProcessStartInfo psi, string workingDir, string? command,
                                   bool closeOnExit = false)
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

        // -NoExit se OMITE cuando el servicio pidió "cerrar la terminal al terminar" (comandos
        // LANZADORES tipo "code ." cuyo único trabajo es abrir otra app — sin -NoExit, pwsh sale solo
        // al terminar y la pestaña queda libre de arrastrar una consola vacía para siempre). Windows
        // Terminal sólo cierra la pestaña sola si el exit code fue 0 (su default `closeOnExit:
        // graceful`): un lanzador que FALLA deja la pestaña abierta mostrando el error, a propósito —
        // no queremos tragarnos un fallo en silencio.
        if (!closeOnExit) psi.ArgumentList.Add("-NoExit");

        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
    }

    /// <summary>
    /// ¿Es <paramref name="hwnd"/> una ventana de Windows Terminal en el escritorio virtual dado?
    /// El filtro de "visible" lo aplica el enumerador (<see cref="WindowMethods.FindVisible"/> /
    /// <see cref="WindowFocuser"/>); acá sólo decidimos clase + escritorio. Una única definición del
    /// criterio, reusada tanto para BUSCAR la ventana (reuse) como para ESPERARLA (ventana nueva).
    ///
    /// La elevación se compara ÚLTIMA (abrir el token de un proceso es lo caro, y así sólo se paga por
    /// ventanas que ya son WT de este desk) y contra el tri-estado de
    /// <see cref="ElevationHelper.IsWindowProcessElevated"/>: "no se pudo saber" NO coincide con
    /// nada. El peor caso de ese criterio es una ventana nueva de más; el del contrario, una pestaña
    /// perdida en la ventana del otro lado.
    /// </summary>
    private static bool IsTerminalOn(IntPtr hwnd, int desktop, bool elevated) =>
        WindowMethods.ClassOf(hwnd) == WtWindowClass
        && VirtualDesktopAccessor.GetWindowDesktopNumber(hwnd) == desktop
        && ElevationHelper.IsWindowProcessElevated(hwnd) == elevated;

    /// <summary>
    /// Primera ventana de Windows Terminal VISIBLE en el escritorio virtual dado y con la elevación
    /// pedida (IntPtr.Zero si ninguna). Mismo patrón "desktop-aware" que <c>QuickActions.OpenDownloads</c>.
    /// </summary>
    private static IntPtr FindTerminalOnDesktop(int desktop, bool elevated) =>
        WindowMethods.FindVisible(hwnd => IsTerminalOn(hwnd, desktop, elevated));

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
    /// tocar con <c>UseShellExecute=false</c>, y acá va en true: es como este camino lanzó siempre
    /// cuando la app corre SIN elevar (un Process.Start común), y es lo que exige el verbo
    /// <c>runas</c> de abajo. El token del hijo ya NO depende de ese flag: corriendo elevados,
    /// <see cref="UnelevatedLauncher"/> lo ignora y crea el proceso con el token de explorer.
    /// Es un mal menor consciente: este camino sólo corre en máquinas SIN Windows Terminal.
    /// En esa máquina el host es conhost, que abre ventana nueva en el escritorio actual.
    ///
    /// <paramref name="elevated"/>: mismo contrato que <see cref="StartTerminal"/> — app elevada →
    /// Process.Start pelado (hereda nuestro token); app sin elevar → <c>runas</c> (UAC). Acá no hace
    /// falta trampolín: no hay alias wt.exe de por medio. Si el UAC se cancela, no se lanza nada.
    /// </summary>
    private static void LaunchDirect(string workingDir, string? command, bool closeOnExit = false,
                                     bool elevated = false)
    {
        // Mismo criterio que AddTabArgs: sin -NoExit, pwsh sale solo al terminar el comando (ver el
        // comentario de ahí para el porqué). Sin comando no hay nada que "terminar" → siempre -NoExit.
        string noExit = command is null || !closeOnExit ? "-NoExit " : "";
        var psi = new ProcessStartInfo(PreferredExe)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDir,
            Arguments = command is null ? "-NoExit" : $"{noExit}-Command \"{command}\"",
        };

        if (!elevated)
        {
            UnelevatedLauncher.Start(psi);
            return;
        }

        try
        {
            if (!ElevationHelper.IsElevated()) psi.Verb = "runas";
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            UnelevatedLauncher.LogFailure(psi.FileName,
                $"lanzamiento ELEVADO (sin Windows Terminal) no realizado: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

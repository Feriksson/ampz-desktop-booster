using System.Collections.Generic;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Services.Attention;

/// <summary>
/// Núcleo de la feature "qué desk necesita atención". Recibe señales crudas del transporte
/// (<see cref="AttentionPipeServer"/>), las resuelve a un escritorio concreto vía el PID, y mantiene
/// el estado "qué desk tiene atención pendiente y de qué nivel". El widget de la barra será un mero
/// CONSUMIDOR de este estado (igual que la barra consume UsageService) — todavía no existe; por ahora
/// el slice dispara un Toast para validar el flujo punta a punta.
///
/// Vive en el core (App.OnStartup), no en la UI. Todos sus métodos se llaman en el hilo de UI: el
/// pipe server marshalea ahí, y el listener de desktops también corre en UI.
/// </summary>
public sealed class AttentionService
{
    private readonly DesktopService _desktops;

    // Estado: desk → nivel pendiente. Un desk guarda el nivel MÁS ALTO mientras no lo visites
    // (ActionNeeded gana sobre Completed: si te bloquearon y además algo terminó, lo que importa es ir).
    private readonly Dictionary<int, AttentionLevel> _pending = new();

    /// <summary>Snapshot de los desks con atención pendiente (lo pintará el widget).</summary>
    public IReadOnlyDictionary<int, AttentionLevel> Pending => _pending;

    /// <summary>Se dispara (en UI) cuando cambia el conjunto de desks pendientes — alta o limpieza.</summary>
    public event Action? Changed;

    public AttentionService(DesktopService desktops)
    {
        _desktops = desktops;
    }

    /// <summary>
    /// Maneja una señal ya parseada: resuelve el desk por el PID (posición REAL de la ventana del
    /// proceso, a prueba de "mismo proyecto en dos desks") y lo marca como pendiente.
    /// </summary>
    public void OnSignal(AttentionSignal signal)
    {
        // Releemos la config en cada señal (esporádicas → no es hot path) para tomar siempre lo último
        // que el usuario dejó en la pestaña Atención, sin cablear recargas. Igual que la pestaña Tareas.
        var settings = AttentionSettings.Load();

        // Feature ON/OFF entera: off → la señal se descarta (ni toast, ni widget, ni sonido).
        if (!settings.Enabled)
            return;

        IntPtr hwnd;
        if (signal.Hwnd != 0)
        {
            // El cliente nos dio la ventana EXACTA (la conocía: capturó el foreground). La usamos
            // directo — más preciso que re-resolver por PID, que con VS Code multiventana es ambiguo
            // y, peor, tardío: re-resolver 2s después elegía la ventana del frente (la del desk al que
            // te habías movido). Con el hwnd fijo, la ventana del disparo se respeta vengas de donde vengas.
            hwnd = new IntPtr(signal.Hwnd);
        }
        else
        {
            // El PID puede mapear a VARIAS ventanas (VS Code multi-ventana comparte el PID del main de
            // Electron). Las juntamos todas y desambiguamos por el cwd (título de la ventana).
            var candidates = WindowMethods.TopLevelWindowsForPid(signal.Pid);
            hwnd = PickWindow(candidates, signal.Cwd);
        }

        int desk = _desktops.GetWindowDesktop(hwnd);

        if (desk < 0)
        {
            // No pudimos ubicar la ventana (proceso headless, ya cerrado, etc.). No hay desk que
            // resaltar — avisamos igual para no perder la señal, pero no tocamos el estado.
            MaybePlay(settings, signal.Level, sameDesk: false); // sin desk no aplica el gate de mismo-desk
            Toasts.Info("Atención (desk no resuelto)", DescribeSource(signal));
            return;
        }

        // ¿El aviso es del desk en el que YA estás parado? Cambia TODO: no hay que "ir" a ningún lado.
        bool sameDesk = desk == _desktops.Current;

        string deskName = _desktops.GetName(desk);
        // Si el desk tiene un proyecto activo en la sesión, va en su PROPIO renglón (tercera línea del
        // toast) para que de un vistazo sepas no sólo DÓNDE sino EN QUÉ. Si no hay proyecto
        // (MAIN/MAILS/MISCS, o un DESK+ sin proyecto), la línea queda vacía → el toast ni la muestra.
        string project = _desktops.GetProject(desk);

        // El TÍTULO es distinto según dónde caiga el aviso:
        //  - OTRO desk → instrucción de navegación: "andá a DESK +N" (te decimos a dónde).
        //  - MISMO desk (estás acá) → mensaje DIRECTO: no te mandamos a ningún lado, ya estás.
        string title;
        if (sameDesk)
            title = signal.Level == AttentionLevel.ActionNeeded
                ? "🔔  Te necesita, acá mismo"
                : "✅  Listo, terminó acá";
        else
            title = signal.Level == AttentionLevel.ActionNeeded
                ? $"🔔  {deskName} te necesita"
                : $"✅  {deskName}: tarea lista";

        // Sonido y toast son INDEPENDIENTES en el caso mismo-desk: podés querer solo el sonido (sin
        // toast) cuando algo pasa donde ya estás, o al revés. Cada uno tiene su propio gate.
        MaybePlay(settings, signal.Level, sameDesk);

        if (!sameDesk || settings.ToastOnSameDesk)
            Toasts.Info(title, DescribeSource(signal), project);

        // Sólo marcamos PENDIENTE si es OTRO desk: el estado "pending" es "desks que tenés que VISITAR".
        // Tu propio desk no se visita — ya estás. Marcarlo dejaría el widget resaltando donde ya estás,
        // y el ClearDesk (que limpia al ENTRAR) nunca se dispararía porque no vas a re-entrar.
        if (!sameDesk)
        {
            if (!_pending.TryGetValue(desk, out var existing) || Outranks(signal.Level, existing))
                _pending[desk] = signal.Level;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Limpia la atención de un desk — lo llama el listener de cambios de desktop cuando ENTRÁS ahí.
    /// "Lo viste, listo": el aviso se apaga solo, sin que tengas que descartarlo a mano.
    /// </summary>
    public void ClearDesk(int desk)
    {
        if (_pending.Remove(desk))
            Changed?.Invoke();
    }

    /// <summary>
    /// Elige, entre las ventanas que comparten el PID, la que de verdad corresponde a la señal. La
    /// pista es el cwd: VS Code muestra el nombre del folder en el título de la ventana, así que la
    /// ventana correcta es la cuyo título contiene el último segmento del cwd. Sin cwd o sin match,
    /// caemos a la primera (mejor algo que nada — es el comportamiento que teníamos).
    /// </summary>
    private static IntPtr PickWindow(IReadOnlyList<IntPtr> candidates, string cwd)
    {
        if (candidates.Count == 0) return IntPtr.Zero;
        if (candidates.Count == 1) return candidates[0]; // sin ambigüedad, no hace falta el cwd

        string folder = LastSegment(cwd);
        if (folder.Length > 0)
        {
            foreach (var c in candidates)
                if (WindowMethods.TextOf(c).Contains(folder, StringComparison.OrdinalIgnoreCase))
                    return c;
        }
        return candidates[0];
    }

    /// <summary>Último segmento de un path (el nombre del folder), tolerante a / y \.</summary>
    private static string LastSegment(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string norm = path.Replace('\\', '/').TrimEnd('/');
        int slash = norm.LastIndexOf('/');
        return slash >= 0 ? norm[(slash + 1)..] : norm;
    }

    /// <summary>ActionNeeded manda sobre Completed. (Si crecen los niveles, esto se vuelve un orden.)</summary>
    private static bool Outranks(AttentionLevel candidate, AttentionLevel current) =>
        candidate == AttentionLevel.ActionNeeded && current == AttentionLevel.Completed;

    /// <summary>Texto secundario del toast: el mensaje de la fuente, o la fuente si no mandó texto.</summary>
    private static string DescribeSource(AttentionSignal s) =>
        s.Message.Length > 0 ? s.Message : s.Source;

    /// <summary>
    /// Reproduce (o no) el sonido de la señal según la config: respeta el master de sonido y el gate
    /// de "mismo desk". El .wav y el volumen salen de los settings; el motor (con volumen) es
    /// AttentionSound. El sonido NUNCA es crítico.
    /// </summary>
    private static void MaybePlay(AttentionSettings s, AttentionLevel level, bool sameDesk)
    {
        if (!s.SoundEnabled) return;                  // master de sonido apagado
        if (sameDesk && !s.SoundOnSameDesk) return;   // no querés ruido si el aviso es de tu propio desk

        string wav = level == AttentionLevel.ActionNeeded ? s.SoundActionNeeded : s.SoundCompleted;
        AttentionSound.Play(wav, s.Volume);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using AmpzDesktopBooster.Desktops;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Par de combos Espacio + Contexto que eligen DÓNDE se guarda un alta (variable o servicio). Lo
/// comparten <see cref="VariableEditWindow"/> y <see cref="ServiceEditWindow"/>: si cada diálogo
/// armara su propia versión, el día que una se corrija la otra seguiría resolviendo el scope distinto
/// ante el MISMO gesto — y lo que está en juego es dónde aterriza el dato, no un detalle visual.
///
/// Mismo criterio que <see cref="LooseNoteDialog"/>: el scope se ELIGE de los que existen, nunca se
/// tipea (un typo crearía un scope fantasma, invisible desde la pestaña Espacios). La diferencia es
/// el primer ítem del Espacio: acá es la GLOBAL, que es un destino real y no "sin etiqueta".
/// </summary>
internal sealed class ScopePicker
{
    private readonly ProjectStore _store;
    private readonly ComboBox _space;
    private readonly ComboBox _context;
    private readonly Action _changed;

    // Primer ítem de cada combo. Se comparan por REFERENCIA, no por texto: un espacio podría llamarse
    // literalmente "Global (compartida)" y no por eso es la global.
    private readonly string _global = Loc.T("Config.VarsGlobal");
    private readonly string _wholeSpace = Loc.T("Scope.NoContext");

    /// <param name="initialScopeKey">Scope donde está parado el que abre el diálogo: es el default,
    /// porque el caso común es "agregar acá" y el picker existe para la excepción.</param>
    /// <param name="changed">Se llama cada vez que cambia el scope elegido (para el renglón "Se guarda en").</param>
    public ScopePicker(ProjectStore store, ComboBox space, ComboBox context,
                       string initialScopeKey, Action changed)
    {
        _store = store;
        _space = space;
        _context = context;
        _changed = changed;

        string project = initialScopeKey, module = "";
        int sep = initialScopeKey.IndexOf(ProjectStore.ScopeSeparator);
        if (sep >= 0)
        {
            project = initialScopeKey[..sep];
            module = initialScopeKey[(sep + 1)..];
        }

        var spaces = new List<string> { _global };
        spaces.AddRange(store.GetHistory().OrderBy(h => h, StringComparer.CurrentCultureIgnoreCase));
        // El scope de origen se ofrece aunque no figure en el historial: si no, abrir el alta parado
        // en él te mandaría el default a OTRO lado sin avisar — justo el error que el picker evita.
        if (project != "" && !spaces.Skip(1).Any(s => Same(s, project))) spaces.Add(project);
        _space.ItemsSource = spaces;
        _space.SelectedItem = project == "" ? _global : spaces.Skip(1).First(s => Same(s, project));

        FillContexts(keep: module);
        // Cambiar de espacio LIMPIA el contexto (mismo criterio que la sesión): arrastrar "Plataforma"
        // a un espacio que quizá ni lo tiene sería guardar en un scope que el usuario no eligió.
        _space.SelectionChanged += (_, _) => { FillContexts(keep: ""); _changed(); };
        _context.SelectionChanged += (_, _) => _changed();
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private string SelectedSpace =>
        _space.SelectedItem is string s && !ReferenceEquals(s, _global) ? s : "";

    private string SelectedContext =>
        _context.SelectedItem is string c && !ReferenceEquals(c, _wholeSpace) ? c : "";

    /// <summary>Key de scope elegida: "" global, "Espacio" o "Espacio/Contexto".</summary>
    public string ScopeKey
    {
        get
        {
            string space = SelectedSpace;
            return space == "" ? ProjectStore.GlobalScope : ProjectStore.ScopeKey(space, SelectedContext);
        }
    }

    /// <summary>Etiqueta legible del scope elegido, para el renglón "Se guarda en".</summary>
    public string Label => LabelOf(ScopeKey);

    public static string LabelOf(string scopeKey) =>
        scopeKey == ProjectStore.GlobalScope ? Loc.T("Config.VarsGlobal") : ProjectStore.PrettyScope(scopeKey);

    /// <summary>Recarga los contextos del espacio elegido, dejando seleccionado <paramref name="keep"/> si existe.</summary>
    private void FillContexts(string keep)
    {
        var contexts = new List<string> { _wholeSpace };
        string space = SelectedSpace;
        if (space != "")
        {
            contexts.AddRange(_store.GetModules(space).Select(m => m.Name)
                                    .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
            if (keep != "" && !contexts.Skip(1).Any(c => Same(c, keep))) contexts.Add(keep); // ver el ctor
        }
        _context.ItemsSource = contexts;
        _context.SelectedItem = keep == "" || space == ""
            ? _wholeSpace
            : contexts.Skip(1).FirstOrDefault(c => Same(c, keep)) ?? _wholeSpace;
        // La global no tiene contextos: deshabilitado se lee mejor que un combo con una sola opción.
        _context.IsEnabled = space != "";
    }
}

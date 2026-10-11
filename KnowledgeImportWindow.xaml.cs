using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using AmpzDesktopBooster.Knowledge;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

/// <summary>
/// Vista previa y confirmación de una importación de bibliotecas. Llega con el archivo YA leído y
/// validado; acá se decide dónde cae y qué hacer ante un choque de nombres, viendo en números qué va
/// a pasar ANTES de que se escriba nada.
///
/// Tres cosas propias:
///   1. Mostrar no escribe. Los números salen de <see cref="KnowledgeStore.PreviewImport"/>, que es
///      el mismo recorrido que la importación real pero sobre una conexión de lectura: lo que se ve
///      acá es lo que después ocurre, y cerrar el diálogo no deja rastro.
///   2. Las dos salidas del choque («copia» / «combinar») llevan CADA UNA su consecuencia con sus
///      números a la vista. Por eso se calculan los dos planes por cada destino, no sólo el elegido:
///      comparar es justamente lo que hay que poder hacer antes de optar.
///   3. El diálogo importa él mismo (igual que KnowledgeEntryWindow guarda él mismo): si la base
///      falla, el motivo aparece acá y queda abierto para reintentar, en vez de cerrarse sobre un error.
/// </summary>
public partial class KnowledgeImportWindow : Window
{
    /// <summary>Lo que se importó y adónde (el texto del destino, para el resumen de la pestaña).</summary>
    public sealed record Result(ImportOutcome Outcome, string DestinationLabel);

    private const double IndentPerLevel = 18;
    private const int MaxNamesListed = 8;

    private readonly KnowledgeStore _store;
    private readonly ExchangeDocument _document;

    private ImportOutcome _copyPlan = ImportOutcome.Failure(ImportStatus.Failed);
    private ImportOutcome _mergePlan = ImportOutcome.Failure(ImportStatus.Failed);
    private Result? _result;
    private bool _loading;

    /// <summary>Un destino posible: la raíz (<see cref="Id"/> null) o una biblioteca existente.</summary>
    private sealed record Destination(long? Id, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Una fila de la lista del archivo, con todo lo que pinta la plantilla ya resuelto.</summary>
    private sealed class TreeRow
    {
        public Thickness Indent { get; init; }
        public string Name { get; init; } = "";
        public FontWeight NameWeight { get; init; }
        public string StateText { get; init; } = "";
        public Brush StateBrush { get; init; } = Brushes.Gray;
        public string CountsText { get; init; } = "";
        public string AccessibleName { get; init; } = "";
    }

    private KnowledgeImportWindow(KnowledgeStore store, ExchangeDocument document, string fileName)
    {
        InitializeComponent();
        _store = store;
        _document = document;

        // Nunca más alto que la pantalla: pasado esto se achica la lista del árbol, no el pie.
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;

        FileText.Text = document.ExportedAt is { } at
            ? string.Format(Loc.T("KnowledgeImport.FileWithDate"), fileName, at.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))
            : string.Format(Loc.T("KnowledgeImport.File"), fileName);

        // Destinos: la raíz y cada biblioteca, en orden de árbol y con su camino entero (dos pueden
        // llamarse igual bajo madres distintas). Arranca en la raíz, que es donde vuelve un respaldo.
        _loading = true;
        DestCombo.Items.Add(new Destination(null, Loc.T("Config.KnowledgeLibMoveRoot")));
        foreach (var (id, label) in LibraryPaths(store.GetLibraries())) DestCombo.Items.Add(new Destination(id, label));
        DestCombo.SelectedIndex = 0;
        _loading = false;

        // Cambiar cualquier decisión retira el aviso de un intento anterior: ya no habla de lo que
        // está en pantalla.
        DestCombo.SelectionChanged += (_, _) => { if (!_loading) { HideNotice(); Recalculate(); } };
        CopyRadio.Checked += (_, _) => { if (!_loading) { HideNotice(); ShowPlan(); } };
        MergeRadio.Checked += (_, _) => { if (!_loading) { HideNotice(); ShowPlan(); } };
        ImportBtn.Click += (_, _) => Import();
        // Cancelar y Esc cierran sin preguntar: acá no hay nada cargado que se pueda perder.
        CancelBtn.Click += (_, _) => Close();

        Recalculate();
        Loaded += (_, _) => DestCombo.Focus();
    }

    /// <summary>
    /// Abre la vista previa sobre <paramref name="owner"/>. Devuelve lo importado, o null si se
    /// canceló (en cuyo caso no se escribió nada).
    /// </summary>
    public static Result? Show(Window owner, KnowledgeStore store, ExchangeDocument document, string fileName)
    {
        var dialog = new KnowledgeImportWindow(store, document, fileName) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    private Destination SelectedDestination => (Destination)DestCombo.SelectedItem;

    private ImportCollisionMode SelectedMode =>
        // Sin choque no hay nada que combinar: se importa como nuevo, sea cual sea la opción marcada.
        _copyPlan.Collisions > 0 && MergeRadio.IsChecked == true ? ImportCollisionMode.Merge : ImportCollisionMode.Copy;

    private ImportOutcome CurrentPlan => SelectedMode == ImportCollisionMode.Merge ? _mergePlan : _copyPlan;

    // ──────────────────────────── Vista previa ────────────────────────────

    /// <summary>Cambió el destino: se rehacen LOS DOS planes (cada opción muestra sus números).</summary>
    private void Recalculate()
    {
        long? destination = SelectedDestination.Id;
        _copyPlan = _store.PreviewImport(_document, new ImportOptions(destination, ImportCollisionMode.Copy));
        _mergePlan = _copyPlan.Status == ImportStatus.Ok && _copyPlan.Collisions > 0
            ? _store.PreviewImport(_document, new ImportOptions(destination, ImportCollisionMode.Merge))
            : _copyPlan;

        bool collision = _copyPlan.Status == ImportStatus.Ok && _copyPlan.Collisions > 0;
        CopyRadio.IsEnabled = collision;
        MergeRadio.IsEnabled = collision;

        // El nombre que choca y el que recibiría la copia salen del plan: son los reales.
        string original = _mergePlan.Lines.FirstOrDefault(l => l.Depth == 0 && l.State == ImportLibraryState.Merged)?.Name ?? "";
        string copyName = _copyPlan.Lines.FirstOrDefault(l => l.State == ImportLibraryState.CreatedAsCopy)?.Name ?? "";
        string total = EntryCount(_copyPlan.CardsAdded + _copyPlan.TermsAdded);

        if (!collision)
        {
            CollisionText.Text = Loc.T("KnowledgeImport.NoCollision");
            CopyDesc.Text = Loc.T("KnowledgeImport.CopyDescIdle");
            MergeDesc.Text = Loc.T("KnowledgeImport.MergeDescIdle");
        }
        else
        {
            bool many = _copyPlan.Collisions > 1;
            CollisionText.Text = many
                ? string.Format(Loc.T("KnowledgeImport.CollisionMany"), _copyPlan.Collisions, original)
                : string.Format(Loc.T("KnowledgeImport.CollisionOne"), original);
            CopyDesc.Text = string.Format(Loc.T(many ? "KnowledgeImport.CopyDescMany" : "KnowledgeImport.CopyDescOne"), copyName, total);
            // Si combinar va a completar ayuda, la consecuencia lo DICE y deja de prometer "nada
            // tuyo se modifica": es la única escritura sobre lo existente y no puede ir escondida.
            MergeDesc.Text = _mergePlan.HelpCompleted > 0
                ? string.Format(Loc.T("KnowledgeImport.MergeDescHelp"),
                    _mergePlan.CardsAdded + _mergePlan.TermsAdded, _mergePlan.EntriesSkipped, _mergePlan.HelpCompleted)
                : string.Format(Loc.T("KnowledgeImport.MergeDesc"),
                    _mergePlan.CardsAdded + _mergePlan.TermsAdded, _mergePlan.EntriesSkipped);
        }

        // El lector de pantalla lee la opción CON su consecuencia, no sólo el título.
        AutomationProperties.SetName(CopyRadio, Loc.T("KnowledgeImport.CopyTitle") + ". " + CopyDesc.Text);
        AutomationProperties.SetName(MergeRadio, Loc.T("KnowledgeImport.MergeTitle") + ". " + MergeDesc.Text);

        ShowPlan();
    }

    /// <summary>Vuelca a la pantalla el plan de la opción elegida: resumen, árbol, listas y el botón.</summary>
    private void ShowPlan()
    {
        var plan = CurrentPlan;
        bool ok = plan.Status == ImportStatus.Ok;

        SummaryText.Text = ok
            ? string.Format(Loc.T("KnowledgeImport.SummaryAdds"), plan.LibrariesCreated, plan.CardsAdded, plan.TermsAdded)
            : "";
        MergeSummaryText.Text = string.Format(Loc.T("KnowledgeImport.SummaryMerge"), plan.LibrariesMerged, plan.EntriesSkipped);
        MergeSummaryText.Visibility = ok && plan.LibrariesMerged > 0 ? Visibility.Visible : Visibility.Collapsed;
        HelpSummaryText.Text = string.Format(Loc.T("KnowledgeImport.SummaryHelp"), plan.HelpCompleted);
        HelpSummaryText.Visibility = ok && plan.HelpCompleted > 0 ? Visibility.Visible : Visibility.Collapsed;

        TreeList.ItemsSource = plan.Lines.Select(BuildRow).ToList();

        NaturesText.Text = string.Format(Loc.T("KnowledgeImport.Natures"),
            Names(plan.NewNatures, "KnowledgeImport.NoneF"), Names(plan.ExistingNatures, "KnowledgeImport.NoneF"));
        LanguagesText.Text = string.Format(Loc.T("KnowledgeImport.Languages"),
            Names(plan.NewLanguages, "KnowledgeImport.NoneM"), Names(plan.ExistingLanguages, "KnowledgeImport.NoneM"));

        // Importar apagado SIEMPRE con su motivo al lado. "Nada para agregar" es un resultado
        // legítimo de combinar dos veces el mismo archivo, y se dice como tal. Si la base no
        // respondió ni para la vista previa, el botón pasa a ser «Reintentar»: sin él, la única
        // forma de volver a consultar sería cambiar el destino a otro y volver.
        bool unavailable = plan.Status == ImportStatus.Failed;
        ImportBtn.Content = Loc.T(unavailable ? "KnowledgeImport.Retry" : "KnowledgeImport.Import");
        ImportBtn.IsEnabled = unavailable || (ok && plan.AddsAnything);
        ReasonText.Text = plan.Status switch
        {
            ImportStatus.DestinationNotFound => Loc.T("KnowledgeImport.ReasonDestGone"),
            ImportStatus.Failed => Loc.T("KnowledgeImport.ReasonUnavailable"),
            _ => plan.AddsAnything ? "" : Loc.T("KnowledgeImport.ReasonNothing"),
        };
    }

    private TreeRow BuildRow(ImportLibraryLine line)
    {
        bool merged = line.State == ImportLibraryState.Merged;
        string state = Loc.T(line.State switch
        {
            ImportLibraryState.Merged => "KnowledgeImport.RowMerged",
            ImportLibraryState.CreatedAsCopy => "KnowledgeImport.RowCopy",
            _ => "KnowledgeImport.RowNew",
        });
        string counts = !merged
            ? string.Format(Loc.T("KnowledgeImport.RowCounts"), line.Cards, line.Terms)
            : line.HelpCompleted > 0
                ? string.Format(Loc.T("KnowledgeImport.RowCountsMergedHelp"), line.Cards + line.Terms, line.Skipped, line.HelpCompleted)
                : string.Format(Loc.T("KnowledgeImport.RowCountsMerged"), line.Cards + line.Terms, line.Skipped);

        return new TreeRow
        {
            Indent = new Thickness(line.Depth * IndentPerLevel, 0, 0, 0),
            Name = line.Name,
            NameWeight = line.Depth == 0 ? FontWeights.SemiBold : FontWeights.Normal,
            StateText = state,
            // Lo que se CREA va en acento; lo que ya existe y sólo recibe, en gris.
            StateBrush = (Brush)FindResource(merged ? "FgMuted" : "Accent"),
            CountsText = counts,
            AccessibleName = string.Format(Loc.T("KnowledgeImport.RowAccessible"), line.Name, state, counts),
        };
    }

    /// <summary>Una lista de nombres para un renglón: los primeros, y "y N más" si son muchos.</summary>
    private static string Names(IReadOnlyList<string> names, string noneKey)
    {
        if (names.Count == 0) return Loc.T(noneKey);
        string shown = string.Join(", ", names.Take(MaxNamesListed));
        return names.Count <= MaxNamesListed
            ? shown
            : string.Format(Loc.T("KnowledgeImport.NamesMore"), shown, names.Count - MaxNamesListed);
    }

    private static string EntryCount(int count) => count == 1
        ? Loc.T("Playbook.EntriesOne")
        : string.Format(Loc.T("Playbook.EntriesMany"), count);

    /// <summary>Las bibliotecas en orden de árbol, cada una con su camino desde la raíz.</summary>
    private static List<(long Id, string Label)> LibraryPaths(IReadOnlyList<Library> libraries)
    {
        var byId = libraries.ToDictionary(l => l.Id);
        var children = libraries.ToLookup(l => l.ParentId);
        var ordered = new List<(long, string)>();
        var seen = new HashSet<long>();

        void Walk(Library library)
        {
            if (!seen.Add(library.Id)) return; // una base tocada a mano con un ciclo no cuelga el diálogo
            ordered.Add((library.Id, string.Join(KnowledgeExchange.PathSeparator, LibraryTree.PathOf(library.Id, byId))));
            foreach (var child in children[library.Id].OrderBy(l => l.Sort).ThenBy(l => l.Id)) Walk(child);
        }

        foreach (var root in children[null].OrderBy(l => l.Sort).ThenBy(l => l.Id)) Walk(root);
        foreach (var orphan in libraries.OrderBy(l => l.Id)) Walk(orphan);
        return ordered;
    }

    // ──────────────────────────── Importar ────────────────────────────

    private void Import()
    {
        if (!ImportBtn.IsEnabled) return;
        if (CurrentPlan.Status == ImportStatus.Failed)
        {
            // «Reintentar»: se vuelve a pedir la vista previa. Nunca se importa sin haberla visto.
            HideNotice();
            Recalculate();
            return;
        }

        var destination = SelectedDestination;
        var outcome = _store.ImportLibraries(_document, new ImportOptions(destination.Id, SelectedMode));
        if (outcome.Status == ImportStatus.Ok)
        {
            _result = new Result(outcome, destination.Label);
            Close();
            return;
        }

        // No entró NADA (una sola transacción). Se dice por qué y el diálogo queda abierto, con la
        // vista previa rehecha: si lo que falló fue que el destino desapareció, ahí se ve.
        NoticeText.Text = outcome.Status == ImportStatus.DestinationNotFound
            ? Loc.T("KnowledgeImport.ReasonDestGone")
            : outcome.FailureDetail == ""
                ? Loc.T("KnowledgeImport.FailedNoDetail")
                : string.Format(Loc.T("KnowledgeImport.Failed"), outcome.FailureDetail);
        NoticeBox.Visibility = Visibility.Visible;
        Recalculate();
    }

    private void HideNotice() => NoticeBox.Visibility = Visibility.Collapsed;
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using AmpzDesktopBooster.Knowledge;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

// ── Pestaña CONOCIMIENTO: importar / exportar bibliotecas ────────────────────────────────────────
//
// Los tres botones del encabezado de la pestaña. Esta parte sólo pone la UI: el formato del archivo
// es de KnowledgeExchange y lo que entra y sale de la base es de KnowledgeStore.
//
//   • Exportar NO tiene confirmación propia: el diálogo de guardar ya es el "¿dónde?", y qué se lleva
//     (qué biblioteca, cuántas sub-bibliotecas y entradas) está escrito junto a los botones ANTES de
//     apretar nada.
//   • Importar valida el archivo ENTERO antes de abrir nada. Un archivo inválido se explica acá, en la
//     pestaña; uno válido abre KnowledgeImportWindow, que muestra qué va a pasar y recién ahí escribe.
//   • El resultado de las dos queda como texto en la pestaña (no en un toast): trae una ruta, y el
//     botón para abrir esa carpeta.
public partial class ConfigWindow
{
    /// <summary>La carpeta del último archivo exportado: adónde lleva «Abrir carpeta».</summary>
    private string? _knExchangeFolder;

    private void InitKnowledgeExchange()
    {
        KnImportBtn.Click += (_, _) => ImportKnLibraries();
        KnExportBtn.Click += (_, _) => ExportKnLibraries(all: false);
        KnExportAllBtn.Click += (_, _) => ExportKnLibraries(all: true);
        KnExchangeOpenFolderBtn.Click += (_, _) => OpenKnExchangeFolder();
        KnExchangeDismissBtn.Click += (_, _) => HideKnExchangeNotice();
    }

    /// <summary>
    /// Deja escrito junto a los botones QUÉ exporta cada uno, con sus números; y si no hay nada que
    /// exportar, por qué están apagados. Todo sale de la foto en memoria: no consulta la base.
    /// </summary>
    private void UpdateKnExchangeScope()
    {
        var library = SelectedKnLibrary;
        bool any = _knLibraries.Count > 0;
        KnExportBtn.IsEnabled = library is not null;
        KnExportAllBtn.IsEnabled = any;

        if (!any || library is null)
        {
            KnExchangeScope.Text = Loc.T(any ? "Config.KnowledgeExportPickReason" : "Config.KnowledgeExportNoneReason");
            return;
        }

        var subtree = KnSubtreeIds(library.Id);
        int subLibraries = subtree.Count - 1;
        int entries = subtree.Sum(id => _knEntryCounts.TryGetValue(id, out int n) ? n : 0);
        string subText = subLibraries switch
        {
            0 => Loc.T("Config.KnowledgeExportSubNone"),
            1 => Loc.T("Config.KnowledgeExportSubOne"),
            _ => string.Format(Loc.T("Config.KnowledgeExportSubMany"), subLibraries),
        };

        KnExchangeScope.Text =
            string.Format(Loc.T("Config.KnowledgeExportScope"), library.Name, subText, KnCount(entries)) + "\n" +
            string.Format(Loc.T("Config.KnowledgeExportAllScope"), KnLibraryCount(_knLibraries.Count),
                KnCount(_knEntryCounts.Values.Sum())) + "\n" +
            Loc.T("Config.KnowledgeExportNoProgress");
    }

    private static string KnLibraryCount(int count) => count == 1
        ? Loc.T("Config.KnowledgeLibrariesOne")
        : string.Format(Loc.T("Config.KnowledgeLibrariesMany"), count);

    // ──────────────────────────── Exportar ────────────────────────────

    private void ExportKnLibraries(bool all)
    {
        var library = all ? null : SelectedKnLibrary;
        if (!all && library is null) return;

        // El contenido se lee ANTES de preguntar dónde guardarlo: si la base no responde o la
        // biblioteca ya no existe, se dice ahora y no después de haber elegido carpeta y nombre.
        var document = _knowledge.ExportLibraries(library?.Id);
        if (document is null)
        {
            ShowKnExchangeNotice(Loc.T("Config.KnowledgeExportFailedRead"), error: true);
            return;
        }
        if (document.Libraries.Count == 0)
        {
            // Se borró desde otro lado (o no queda ninguna): la pantalla estaba vieja.
            ShowKnExchangeNotice(Loc.T("Config.KnowledgeErrNotFound"), error: true);
            RefreshKnowledge();
            return;
        }
        if (document.Depth() > KnowledgeExchange.MaxDepth)
        {
            // Se rechaza acá y no al importar: un respaldo que después no se puede restaurar es
            // peor que no tener respaldo, porque uno cree que lo tiene.
            ShowKnExchangeNotice(string.Format(Loc.T("Config.KnowledgeExportTooDeep"), KnowledgeExchange.MaxDepth), error: true);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = all
                ? Loc.T("Config.KnowledgeExportAllDialogTitle")
                : string.Format(Loc.T("Config.KnowledgeExportDialogTitle"), library!.Name),
            Filter = Loc.T("Config.KnowledgeExchangeFilter"),
            FileName = KnowledgeExchange.SuggestFileName(
                all ? Loc.T("Config.KnowledgeExportAllFileName") : library!.Name, DateTime.Now),
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        WriteKnExport(document, dialog.FileName, asList: all);
    }

    /// <summary>Escribe el archivo y deja el resultado a la vista: dónde quedó y cuánto lleva, o por qué no se pudo.</summary>
    private void WriteKnExport(ExchangeDocument document, string path, bool asList)
    {
        try
        {
            // No por SafeFile: ése deja la versión anterior como «archivo.json.bak», que tiene sentido
            // junto a los datos de la app (es la red de un archivo que se reescribe solo) pero acá
            // es basura en una carpeta del USUARIO — pisó su exportación porque quiso.
            KnowledgeExchange.WriteFile(path, KnowledgeExchange.Serialize(document, asList));
        }
        catch (Exception ex)
        {
            // Escritura atómica: si falló, en esa ruta quedó lo que había (o nada), nunca medio archivo.
            ShowKnExchangeNotice(string.Format(Loc.T("Config.KnowledgeExportFailedWrite"), path, ex.Message), error: true);
            return;
        }

        ShowKnExchangeNotice(
            string.Format(Loc.T("Config.KnowledgeExportDone"), path,
                KnLibraryCount(document.CountLibraries()), KnCount(document.CountEntries())),
            error: false, folder: Path.GetDirectoryName(path));
    }

    private void OpenKnExchangeFolder()
    {
        if (_knExchangeFolder is not { } folder) return;
        // Por PathOpener y no un Process.Start directo: la app corre elevada, y él abre el Explorer
        // de-elevado y en el monitor donde está el usuario.
        if (Desktops.PathOpener.Open(folder) != Desktops.PathOpener.Result.Opened)
            ShowKnExchangeNotice(string.Format(Loc.T("Config.KnowledgeExchangeOpenFolderFailed"), folder), error: true);
    }

    // ──────────────────────────── Importar ────────────────────────────

    private void ImportKnLibraries()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Config.KnowledgeImportDialogTitle"),
            Filter = Loc.T("Config.KnowledgeExchangeFilter"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        ImportKnFile(dialog.FileName);
    }

    private void ImportKnFile(string path)
    {
        string fileName = Path.GetFileName(path);

        // Se lee y se valida TODO antes de tocar la base o abrir nada: un archivo a medias no llega
        // ni a la vista previa.
        var read = KnowledgeExchange.ReadFile(path);
        if (read.Document is not { } document)
        {
            ShowKnExchangeNotice(string.Format(Loc.T("Config.KnowledgeImportInvalid"), fileName,
                KnExchangeErrorText(read.Error ?? new ExchangeError(ExchangeErrorKind.FileUnreadable))), error: true);
            return;
        }

        if (KnowledgeImportWindow.Show(this, _knowledge, document, fileName) is not { } result)
        {
            // Canceló. Igual se relee: el diálogo estuvo abierto un rato y la base pudo cambiar.
            RefreshKnowledge();
            return;
        }

        var outcome = result.Outcome;
        // Queda elegida la biblioteca importada (o la que recibió lo combinado): es lo que se vino a ver.
        RefreshKnowledge(selectLibraryId: outcome.RootLibraryIds.Count > 0 ? outcome.RootLibraryIds[0] : null,
            selectEntryIds: Array.Empty<long>());
        ShowKnExchangeNotice(string.Format(Loc.T("Config.KnowledgeImportDone"), fileName, result.DestinationLabel,
            outcome.LibrariesCreated, outcome.CardsAdded, outcome.TermsAdded, outcome.EntriesSkipped), error: false);
        KnLibList.Focus();
    }

    /// <summary>
    /// El motivo por el que un archivo no se pudo leer, dicho para una persona: QUÉ está mal y, cuando
    /// el problema es de una biblioteca o una entrada puntual, DÓNDE. Nunca el texto de una excepción
    /// solo (el único que lo incluye es "no se pudo leer el archivo", y va detrás de la explicación).
    /// </summary>
    internal static string KnExchangeErrorText(ExchangeError error)
    {
        string where = error.Entry > 0
            ? string.Format(Loc.T("Config.KnowledgeImportWhereEntry"), error.Library, error.Entry)
            : error.Library != ""
                ? string.Format(Loc.T("Config.KnowledgeImportWhereLibrary"), error.Library)
                : Loc.T("Config.KnowledgeImportWhereTop");
        string max = KnowledgeExchange.MaxDepth.ToString(CultureInfo.CurrentCulture);

        string text = error.Kind switch
        {
            ExchangeErrorKind.FileTooLarge => string.Format(Loc.T("Config.KnowledgeImportErrTooLarge"),
                error.Detail, KnowledgeExchange.MaxFileBytes / (1024 * 1024)),
            ExchangeErrorKind.FileUnreadable => string.Format(Loc.T("Config.KnowledgeImportErrUnreadable"), error.Detail),
            ExchangeErrorKind.EmptyFile => Loc.T("Config.KnowledgeImportErrEmpty"),
            ExchangeErrorKind.MalformedJson => string.Format(Loc.T("Config.KnowledgeImportErrMalformed"), error.Line, error.Column),
            ExchangeErrorKind.NotAnObject => Loc.T("Config.KnowledgeImportErrNotAnObject"),
            ExchangeErrorKind.WrongFormat => error.Detail == ""
                ? string.Format(Loc.T("Config.KnowledgeImportErrFormatMissing"), KnowledgeExchange.FormatName)
                : string.Format(Loc.T("Config.KnowledgeImportErrFormat"), error.Detail, KnowledgeExchange.FormatName),
            ExchangeErrorKind.UnsupportedVersion => string.Format(Loc.T("Config.KnowledgeImportErrVersion"),
                error.Detail, KnowledgeExchange.FormatVersion),
            ExchangeErrorKind.NoLibraries => Loc.T("Config.KnowledgeImportErrNoLibraries"),
            ExchangeErrorKind.MissingField => string.Format(Loc.T("Config.KnowledgeImportErrMissingField"), error.Field, where),
            ExchangeErrorKind.WrongType => string.Format(Loc.T("Config.KnowledgeImportErrWrongType"), error.Field, where),
            // Acá el "dónde" ES la biblioteca sin nombre: se cita su posición, no "en la biblioteca…".
            ExchangeErrorKind.EmptyName => string.Format(Loc.T("Config.KnowledgeImportErrEmptyName"), error.Library),
            ExchangeErrorKind.EmptyFront => string.Format(Loc.T("Config.KnowledgeImportErrEmptyFront"), where),
            ExchangeErrorKind.UnknownKind => string.Format(Loc.T("Config.KnowledgeImportErrUnknownKind"), error.Detail, where),
            ExchangeErrorKind.EmptyLanguageName => string.Format(Loc.T("Config.KnowledgeImportErrEmptyLanguage"), where),
            ExchangeErrorKind.TooDeep => string.Format(Loc.T("Config.KnowledgeImportErrTooDeep"), max, where),
            ExchangeErrorKind.TooManyEntries => string.Format(Loc.T("Config.KnowledgeImportErrTooManyEntries"),
                KnowledgeExchange.MaxEntries.ToString("N0", CultureInfo.CurrentCulture)),
            _ => Loc.T("Config.KnowledgeImportErrNotAnObject"),
        };

        // Una biblioteca cuyo nombre no se pudo leer figura por su posición ("#3"): se aclara qué es.
        return error.Library.Contains('#') ? text + " " + Loc.T("Config.KnowledgeImportPositionLegend") : text;
    }

    // ──────────────────────────── El aviso de resultado ────────────────────────────

    /// <summary>
    /// Muestra el resultado de una importación / exportación arriba de las sub-pestañas. Con
    /// <paramref name="folder"/> aparece «Abrir carpeta». Un error lleva la marca en rojo además del
    /// texto (que ya dice qué pasó: el color no es lo único que lo distingue).
    /// </summary>
    private void ShowKnExchangeNotice(string text, bool error, string? folder = null)
    {
        _knExchangeFolder = error || string.IsNullOrEmpty(folder) ? null : folder;
        KnExchangeNoticeText.Text = text;
        KnExchangeNotice.BorderBrush = (Brush)KnExchangeNotice.FindResource(error ? "KnDanger" : "Accent");
        KnExchangeOpenFolderBtn.Visibility = _knExchangeFolder is null ? Visibility.Collapsed : Visibility.Visible;
        KnExchangeNotice.Visibility = Visibility.Visible;
    }

    private void HideKnExchangeNotice()
    {
        _knExchangeFolder = null;
        KnExchangeNotice.Visibility = Visibility.Collapsed;
        // El botón que tenía el foco acaba de desaparecer: el foco vuelve a un lugar conocido.
        KnImportBtn.Focus();
    }
}

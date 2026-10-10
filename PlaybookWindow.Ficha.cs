using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Knowledge;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

// ── Playbooker · 5 · FICHA ───────────────────────────────────────────────────────────────────────
//
// Una entrada para LEER, sin jugarla: es a lo que lleva Enter sobre un resultado de la búsqueda.
// A la izquierda, la entrada completa (en un término: palabra, tipo, definición, traducciones,
// contexto de uso y origen) con la tipografía de la tarjeta del quiz. A la derecha, cuán firme está
// vista desde cada lado y su historial de repasos con fechas — "lo sabía en marzo y se me fue".
//
// Leer NO anota nada: mirar una ficha no dice cuánto se la sabe, igual que "saltar" en el quiz. El
// historial sólo lo escriben las respuestas del quiz.
public partial class PlaybookWindow
{
    /// <summary>Cuántos repasos se listan. El resto se resume en "y N más": el historial es para ver
    /// cómo viene últimamente, no para auditarlo, y con este tope la columna entra sin scroll.</summary>
    private const int HistoryCap = 10;

    // Lo que se está leyendo: los resultados de la búsqueda de la que se vino, y cuál de ellos.
    private List<Entry> _fichaEntries = new();
    private int _fichaIndex;

    private void InitFicha()
    {
        FichaEditBtn.Click += (_, _) => EditFichaEntry();
        FichaBackBtn.Click += (_, _) => CloseFicha();
        FichaPrevBtn.Click += (_, _) => StepFicha(-1);
        FichaNextBtn.Click += (_, _) => StepFicha(1);
        AutomationProperties.SetName(FichaEditBtn, Loc.T("Playbook.FichaEdit"));
    }

    private void OpenFicha(IReadOnlyList<Entry> results, int index)
    {
        if (results.Count == 0) return;
        _fichaEntries = results.ToList();
        _fichaIndex = Math.Clamp(index, 0, _fichaEntries.Count - 1);
        ShowStage(Stage.Ficha);
        RenderFicha();
    }

    /// <summary>Esc / Volver: a la búsqueda, con el mismo texto y parado en la entrada que se estaba leyendo.</summary>
    private void CloseFicha()
    {
        long? current = _fichaIndex < _fichaEntries.Count ? _fichaEntries[_fichaIndex].Id : null;
        BackToSearch(reload: false, selectEntryId: current);
    }

    private void StepFicha(int delta)
    {
        int next = _fichaIndex + delta;
        if (next < 0 || next >= _fichaEntries.Count) return; // en las puntas no hay vuelta: el botón ya está apagado
        _fichaIndex = next;
        RenderFicha();
    }

    /// <summary>
    /// Edita la entrada que se está leyendo, con el mismo diálogo de la config. Al volver se relee
    /// TODO (pudo cambiar el texto, la naturaleza o las traducciones) y la ficha se vuelve a pintar
    /// con lo guardado; si entre tanto la entrada dejó de existir, se vuelve a la búsqueda.
    /// </summary>
    private void EditFichaEntry()
    {
        if (_fichaIndex >= _fichaEntries.Count) return;
        long id = _fichaEntries[_fichaIndex].Id;
        // Se edita la versión de la BASE, no la foto de la búsqueda: si cambió por otro lado, el
        // diálogo tiene que abrir con lo que hay ahora.
        if (_store.GetEntry(id) is not { } current)
        {
            BackToSearch(reload: true);
            return;
        }

        KnowledgeEntryWindow.ShowEdit(this, _store, current);

        LoadData();
        // Los vecinos de ← → también se refrescan; los que ya no existan salen de la lista.
        _fichaEntries = _fichaEntries.Where(e => _entries.ContainsKey(e.Id)).Select(e => _entries[e.Id]).ToList();
        int index = _fichaEntries.FindIndex(e => e.Id == id);
        if (index < 0)
        {
            BackToSearch(reload: false);
            return;
        }
        _fichaIndex = index;
        RenderFicha();
    }

    private void OnFichaKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseFicha();
                break;
            case Key.E:
                e.Handled = true;
                EditFichaEntry();
                break;
            case Key.Left:
                e.Handled = true;
                StepFicha(-1);
                break;
            case Key.Right:
                e.Handled = true;
                StepFicha(1);
                break;
            // Enter sobre un botón enfocado es de ese botón; suelto, acá no hace nada (leer no tiene
            // "siguiente paso").
        }
    }

    private void RenderFicha()
    {
        var entry = _fichaEntries[_fichaIndex];
        bool term = entry.Kind == EntryKind.Term;

        FichaKindText.Text = Loc.T(term ? "Playbook.KindTerm" : "Playbook.KindCard");
        FichaPathText.Text = PathText(entry.LibraryId);
        string nature = NatureName(entry);
        FichaNatureText.Text = nature;
        FichaNatureChip.Visibility = nature == "" ? Visibility.Collapsed : Visibility.Visible;

        // ── La entrada ── Los dos lados a la vista (acá no hay pregunta que cuidar) y, en un término,
        // el resto de su ficha. Mismos helpers que la tarjeta del quiz: se lee igual en los dos lados.
        var translations = term ? _store.GetTranslations(entry.Id) : Array.Empty<Translation>();
        FichaPanel.Children.Clear();
        AddSide(FichaPanel, entry, front: true, term, question: true);
        AddSide(FichaPanel, entry, front: false, term, question: false, topMargin: 22);
        if (term) AddTermSheet(FichaPanel, entry, translations);

        // ── Solidez por lado + historial ── Una consulta por la entrada que se abre (no por fila).
        var reviews = _store.GetReviews(new[] { entry.Id });
        var states = Solidity.ComputeAll(reviews, DateTime.UtcNow);
        FichaSidePanel.Children.Clear();
        RenderFichaSolidity(entry, term, states);
        RenderFichaHistory(term, reviews);

        FichaPositionText.Text = string.Format(Loc.T("Playbook.FichaPosition"), _fichaIndex + 1, _fichaEntries.Count);
        FichaPrevBtn.IsEnabled = _fichaIndex > 0;
        FichaNextBtn.IsEnabled = _fichaIndex < _fichaEntries.Count - 1;

        FichaScroll.ScrollToTop();
        UpdateLayout();
        FichaScroll.Focus();
        Keyboard.Focus(FichaScroll);
    }

    /// <summary>Cómo se llama un lado en la ficha: "Término" / "Definición" (o "Frente" / "Dorso" en
    /// una card), y el nombre del idioma cuando lo que se mostró fue una traducción.</summary>
    private string SideName(string side, bool term) => side switch
    {
        ReviewSide.Front => Loc.T(term ? "Playbook.FieldTerm" : "Playbook.SideFront"),
        ReviewSide.Back => Loc.T(term ? "Playbook.FieldDefinition" : "Playbook.SideBack"),
        _ => LanguageOf(side) is { Length: > 0 } language ? language : Loc.T("Playbook.FichaUnknownLanguage"),
    };

    /// <summary>
    /// Una fila por lado: frente y dorso SIEMPRE (jugados o no — "sin jugar" es un dato), y los de
    /// idioma sólo si alguna vez se jugaron. Cada lado tiene su propia solidez: saber una palabra
    /// viéndola no es evocarla desde su definición.
    /// </summary>
    private void RenderFichaSolidity(Entry entry, bool term,
        IReadOnlyDictionary<(long EntryId, string Side), SolidityState> states)
    {
        FichaSidePanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("BlockTitle"),
            Text = Loc.T("Playbook.FichaSolidityTitle"),
        });
        FichaSidePanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("BlockNote"),
            Text = Loc.T("Playbook.FichaSolidityNote"),
            Margin = new Thickness(0, 2, 0, 10),
        });

        var sides = new List<string> { ReviewSide.Front, ReviewSide.Back };
        sides.AddRange(states.Keys.Select(k => k.Side).Where(s => s is not (ReviewSide.Front or ReviewSide.Back))
            .OrderBy(s => SideName(s, term), StringComparer.CurrentCultureIgnoreCase));

        foreach (string side in sides)
        {
            var state = Solidity.StateOf(states, entry.Id, side);
            string name = SideName(side, term);
            string value = state.Played ? Percent(state.Value) : Loc.T("Playbook.NotPlayed");

            var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            row.Children.Add(new TextBlock
            {
                Text = name, FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // La barra sólo si hay algo que medir: sin jugar NO es una barra en cero.
            if (state.Played)
            {
                var track = new Border
                {
                    Width = BarTrackWidth, Height = 7, CornerRadius = new CornerRadius(3),
                    Background = (Brush)FindResource("SoftLineBrush"), VerticalAlignment = VerticalAlignment.Center,
                    Child = new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3),
                        Width = Math.Clamp(state.Value / Solidity.Max, 0, 1) * BarTrackWidth,
                        Background = (Brush)FindResource("AccentBrush"),
                    },
                };
                Grid.SetColumn(track, 1);
                row.Children.Add(track);
            }

            var number = new TextBlock
            {
                Text = value, FontSize = 15, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(state.Played ? "FgBrush" : "MutedBrush"),
            };
            Grid.SetColumn(number, 2);
            row.Children.Add(number);

            AutomationProperties.SetName(row, string.Format(Loc.T("Playbook.FichaSideAccessible"), name, value));
            FichaSidePanel.Children.Add(row);
        }
    }

    /// <summary>
    /// Los últimos repasos, del más nuevo al más viejo: cuándo, qué lado se mostró y qué se respondió
    /// (con el número y el texto de la respuesta; el cuadradito de color sólo acompaña).
    /// </summary>
    private void RenderFichaHistory(bool term, IReadOnlyList<Review> reviews)
    {
        FichaSidePanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("BlockTitle"),
            Text = Loc.T("Playbook.FichaHistoryTitle"),
            Margin = new Thickness(0, 14, 0, 8),
        });

        if (reviews.Count == 0)
        {
            FichaSidePanel.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("BlockNote"),
                FontSize = 15,
                Text = Loc.T("Playbook.FichaHistoryEmpty"),
            });
            return;
        }

        var culture = DateCulture;
        foreach (var review in reviews.OrderByDescending(r => r.ReviewedAt).ThenByDescending(r => r.Id).Take(HistoryCap))
        {
            int grade = Math.Clamp((int)review.Grade, 1, 4);
            string date = DateTime.SpecifyKind(review.ReviewedAt, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy", culture);
            string side = SideName(review.Side, term);
            string answer = string.Format(Loc.T("Playbook.GradeAccessible"), grade, GradeLabel((Grade)grade));

            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            // Anchos FIJOS para la fecha y la respuesta: así las tres cosas quedan en columna y el
            // historial se lee de arriba abajo (con la respuesta pegada a la derecha, cada renglón
            // empezaba en otro lado según el largo del texto).
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(152) });

            row.Children.Add(new TextBlock { Text = date, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var sideText = new TextBlock
            {
                Text = side, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(sideText, 1);
            row.Children.Add(sideText);

            var swatch = new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(10, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)FindResource("Grade" + grade.ToString(CultureInfo.InvariantCulture) + "Brush"),
            };
            Grid.SetColumn(swatch, 2);
            row.Children.Add(swatch);

            var answerText = new TextBlock
            {
                Text = answer, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(answerText, 3);
            row.Children.Add(answerText);

            AutomationProperties.SetName(row, string.Format(Loc.T("Playbook.FichaHistoryAccessible"), date, side, answer));
            FichaSidePanel.Children.Add(row);
        }

        if (reviews.Count > HistoryCap)
            FichaSidePanel.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("BlockNote"),
                Margin = new Thickness(0, 2, 0, 0),
                Text = string.Format(Loc.T("Playbook.FichaHistoryMore"), reviews.Count - HistoryCap),
            });
    }
}

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Knowledge;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

// ── Playbooker · la AYUDA del quiz (tecla H) ─────────────────────────────────────────────────────
//
// Nació del primer uso real: el frente de una card trae a veces palabras que uno no conoce, y la nota
// terminaba midiendo vocabulario en vez del concepto que la card enseña — o mandaba a buscar afuera
// de la app. La ayuda muestra, sin salir de la pregunta, lo que la entrada trae CARGADO para eso:
//
//   • en una CARD, su glosario: hasta cinco palabras difíciles con una definición breve;
//   • en un TÉRMINO, su origen: de dónde viene la palabra y cómo se compone.
//
// El contenido se guarda con la entrada (lo escribe quien la carga, o el prompt de importación): acá
// no se genera nada, sólo se muestra.
//
// ⚠ LA AYUDA NUNCA REGALA LA RESPUESTA. Antes de revelar sólo se puede abrir cuando el lado que se
// muestra es el FRENTE. Con el dorso o una traducción a la vista, lo que hay que evocar es justamente
// el frente: el glosario está escrito sobre sus palabras y el origen de un término deletrea la palabra
// misma. Ahí el botón queda apagado CON SU MOTIVO ESCRITO al lado, y se habilita al ver la respuesta.
//
// Usarla no cambia la nota ni la solidez. Queda anotado en el repaso (Review.Helped) sólo cuando se
// abrió ANTES de revelar: abrirla después es leer, no ayudarse.
public partial class PlaybookWindow
{
    private enum HelpState
    {
        /// <summary>Hay ayuda y se puede abrir ahora.</summary>
        Available,
        /// <summary>Hay ayuda, pero con este lado a la vista delataría la respuesta: espera al revelado.</summary>
        LockedUntilReveal,
        /// <summary>La entrada no tiene ayuda cargada.</summary>
        Empty,
    }

    // Todos los glosarios, leídos UNA vez con el resto de los datos (ver LoadData): el quiz pregunta
    // por el de cada card que muestra, y eso no puede ser una consulta por card.
    private IReadOnlyDictionary<long, IReadOnlyList<GlossaryItem>> _glossaries =
        new Dictionary<long, IReadOnlyList<GlossaryItem>>();

    private bool _helperOpen;          // arranca cerrada en CADA tarjeta
    private bool _helpedBeforeReveal;  // se abrió antes de revelar: es lo que se anota en el repaso

    private void InitHelper()
    {
        HelpBtn.Click += (_, _) => ToggleHelp();
    }

    private IReadOnlyList<GlossaryItem> GlossaryOf(Entry entry) =>
        entry.Kind == EntryKind.Card && _glossaries.TryGetValue(entry.Id, out var items)
            ? items
            : Array.Empty<GlossaryItem>();

    private bool HasHelp(Entry entry) => entry.Kind == EntryKind.Term
        ? !string.IsNullOrWhiteSpace(entry.Origin)
        : GlossaryOf(entry).Count > 0;

    /// <summary>La regla de arriba, en un solo lugar: la consultan la tecla, el botón y la pista del pie.</summary>
    private HelpState HelpStateOf(QuizCard card)
    {
        if (!HasHelp(card.Entry)) return HelpState.Empty;
        return _revealed || card.Side == ReviewSide.Front ? HelpState.Available : HelpState.LockedUntilReveal;
    }

    private QuizCard? CurrentCard => _index >= 0 && _index < _round.Count ? _round[_index] : null;

    private bool HelpAvailableNow => _stage == Stage.Quiz && CurrentCard is { } card && HelpStateOf(card) == HelpState.Available;

    /// <summary>La tarjeta nueva: la pregunta sola, con la ayuda cerrada y su lugar ya previsto.</summary>
    private void ShowQuestionWithHelpClosed(QuizCard card)
    {
        _helperOpen = false;
        _helpedBeforeReveal = false;
        CenterCard();
        RenderCard(card);
        ReserveHelpRoom(card);
    }

    /// <summary>
    /// Le deja LUGAR a la ayuda antes de que nadie la pida. Parte de la tarjeta recién pintada, con la
    /// ayuda cerrada y centrada: si su ayuda no entraría debajo sin correr nada, la tarjeta se ubica
    /// desde ya un poco más arriba —lo justo— para que al abrirla la pregunta no se mueva ni un píxel.
    /// Con una ayuda corta (lo habitual) no hace falta y queda centrada como siempre; una tarjeta sin
    /// ayuda disponible no cambia en nada. Vale igual para la pregunta sola y para la tarjeta ya revelada.
    /// </summary>
    private void ReserveHelpRoom(QuizCard card)
    {
        if (_helperOpen || HelpStateOf(card) != HelpState.Available) return;

        // Se mide sin la ayuda y con ella puesta. Son dos armados más de una tarjeta de pocos
        // elementos: nada, comparado con que la pregunta salte justo cuando se la está leyendo.
        UpdateLayout();
        double closed = CardPanel.ActualHeight;
        _helperOpen = true;
        RenderCard(card);
        UpdateLayout();
        double open = CardPanel.ActualHeight;
        _helperOpen = false;
        RenderCard(card);

        double viewport = CardScroll.ViewportHeight;
        if (viewport <= 0) return; // todavía sin medir: queda centrada
        PinCard(Math.Min((viewport - closed) / 2, viewport - open));
    }

    /// <summary>
    /// Recién revelada, con la ayuda abierta: si así la tarjeta ya no entra entera, la ayuda se
    /// cierra sola. Lo que se pidió con Espacio es la RESPUESTA, y una respuesta que queda debajo del
    /// borde hasta que se scrollea es justo lo que esta ventana no admite. Sigue a una tecla (H). En el
    /// alto de referencia no pasa con contenido normal; es para pantallas chicas y textos muy largos.
    /// </summary>
    private void CloseHelpIfAnswerDoesNotFit(QuizCard card)
    {
        if (!_helperOpen) return;
        UpdateLayout();
        if (CardScroll.ViewportHeight <= 0 || CardPanel.ActualHeight <= CardScroll.ViewportHeight) return;
        _helperOpen = false;
        RenderCard(card);
    }

    /// <summary>
    /// Abre o cierra la ayuda de la tarjeta actual. La pregunta queda DONDE ESTÁ: la tarjeta se ancla
    /// a su posición de ese momento y la ayuda aparece (o se va) debajo. Sólo si así no entrara se
    /// sube lo indispensable. Sin ayuda disponible no hace nada — el motivo ya está escrito junto al botón.
    /// </summary>
    private void ToggleHelp()
    {
        if (_advancing || !HelpAvailableNow || CurrentCard is not { } card) return;

        double top = CardTop();
        _helperOpen = !_helperOpen;
        if (_helperOpen && !_revealed) _helpedBeforeReveal = true;

        RenderCard(card);
        PinCard(top);
        UpdateLayout();
        PinCard(Math.Min(top, CardScroll.ViewportHeight - CardPanel.ActualHeight));

        UpdateHelpControls();
        UpdateHint();
        // El foco vuelve a la tarjeta (un click en el botón se lo había llevado): Espacio y 1..4
        // siguen valiendo igual con la ayuda abierta.
        CardScroll.Focus();
        Keyboard.Focus(CardScroll);
    }

    /// <summary>Cierra la ayuda si está abierta (el primer Esc). Devuelve si había algo que cerrar.</summary>
    private bool CloseHelpIfOpen()
    {
        if (!_helperOpen) return false;
        ToggleHelp();
        return true;
    }

    /// <summary>
    /// El botón y su motivo. Apagado SIEMPRE con el porqué a la vista, en texto y al lado (no en un
    /// tooltip): un botón gris sin explicación se lee igual que uno roto.
    /// </summary>
    private void UpdateHelpControls()
    {
        if (CurrentCard is not { } card) return;
        var state = HelpStateOf(card);
        string reason = state switch
        {
            HelpState.LockedUntilReveal => Loc.T("Playbook.HelpLockedReason"),
            HelpState.Empty => Loc.T("Playbook.HelpEmptyReason"),
            _ => "",
        };

        HelpBtn.IsEnabled = state == HelpState.Available;
        // Abierta / cerrada se dice con el TEXTO del botón, no con un color.
        string label = Loc.T(_helperOpen ? "Playbook.HelpHide" : "Playbook.Help");
        HelpBtnText.Text = label;
        HelpKeycap.Opacity = HelpBtn.IsEnabled ? 1 : 0.45;
        HelpReasonText.Text = reason;
        AutomationProperties.SetName(HelpBtn, reason == "" ? label : label + ". " + reason);
    }

    /// <summary>La pista del pie durante el quiz. Nombra la H sólo si AHORA hay ayuda que abrir.</summary>
    private string QuizHint()
    {
        string key = _revealed ? "Playbook.HintQuizAnswer" : "Playbook.HintQuizQuestion";
        if (HelpAvailableNow) key += _helperOpen ? "HelpOpen" : "Help";
        return Loc.T(key);
    }

    // ── Dónde queda la tarjeta dentro de su panel ──

    private void CenterCard()
    {
        CardPanel.VerticalAlignment = VerticalAlignment.Center;
        CardPanel.Margin = new Thickness(0);
    }

    private void PinCard(double top)
    {
        CardPanel.VerticalAlignment = VerticalAlignment.Top;
        CardPanel.Margin = new Thickness(0, Math.Max(0, Math.Round(top)), 0, 0);
    }

    /// <summary>A cuánto del borde de arriba de su panel está hoy el contenido de la tarjeta.</summary>
    private double CardTop()
    {
        UpdateLayout();
        return VisualTreeHelper.GetParent(CardPanel) is UIElement host
            ? CardPanel.TranslatePoint(new Point(0, 0), host).Y + CardScroll.VerticalOffset
            : 0;
    }

    // ── Lo que se muestra ──

    /// <summary>
    /// El bloque de ayuda de la tarjeta, que va justo debajo de la pregunta. Se distingue del resto
    /// por su recuadro y su título ("Ayuda · …"), no sólo por un color.
    /// </summary>
    private Border BuildHelpBlock(Entry entry)
    {
        bool term = entry.Kind == EntryKind.Term;
        var panel = new StackPanel();
        AddLabel(panel, Loc.T(term ? "Playbook.HelpOriginTitle" : "Playbook.HelpGlossaryTitle"));
        if (term)
            panel.Children.Add(new TextBlock
            {
                Text = entry.Origin, FontSize = 19, LineHeight = 28, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("FgBrush"),
            });
        else
            panel.Children.Add(BuildGlossaryList(GlossaryOf(entry)));

        var block = new Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(18, 13, 18, 15),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("BgBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Child = panel,
        };
        AutomationProperties.SetName(block, Loc.T("Playbook.HelpRegionAccessible"));
        return block;
    }

    /// <summary>
    /// El glosario como lista de definiciones: la palabra a la izquierda (en negrita: es lo que se
    /// busca con la vista) y su definición al lado, todas las palabras en una misma columna. Lo usan
    /// la ayuda del quiz y la ficha de lectura.
    /// </summary>
    private FrameworkElement BuildGlossaryList(IReadOnlyList<GlossaryItem> items)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            double top = i == 0 ? 0 : 8;
            var word = new TextBlock
            {
                Text = items[i].Term, FontSize = 17, LineHeight = 25, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 250, Margin = new Thickness(0, top, 22, 0),
                Foreground = (Brush)FindResource("FgBrush"),
            };
            var definition = new TextBlock
            {
                Text = items[i].Definition, FontSize = 17, LineHeight = 25,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
                Foreground = (Brush)FindResource("FgBrush"),
            };
            // Un lector de pantalla lee el renglón entero ("webhook: aviso automático…"), no dos sueltos.
            AutomationProperties.SetName(word, string.Format(Loc.T("Playbook.HelpItemAccessible"), items[i].Term, items[i].Definition));
            Grid.SetRow(word, i);
            Grid.SetRow(definition, i);
            Grid.SetColumn(definition, 1);
            grid.Children.Add(word);
            grid.Children.Add(definition);
        }
        return grid;
    }
}

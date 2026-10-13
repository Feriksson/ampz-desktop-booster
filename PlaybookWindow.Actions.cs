using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AmpzDesktopBooster.Knowledge;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

// ── Playbooker · EDITAR, ELIMINAR y ANOTAR UN PROBLEMA sin salir de donde se está ────────────────
//
// Dos pedidos del uso real, que son el mismo dolor: jugando aparece una card mal escrita, y para
// arreglarla había que cortar la ronda, ir a la config, buscarla, corregirla y volver a empezar. En
// la práctica no se arreglaba. Ahora, parado en la entrada (en el quiz o en su ficha):
//
//   E · EDITAR      el diálogo de siempre, encima; al volver la tarjeta se repinta EN SU LUGAR
//                   (misma posición en la ronda, revelada o no como estaba).
//   X · ELIMINAR    pide una segunda X (PlaybookWindow.Guard.cs): se va la entrada con su historial.
//   R · PROBLEMA    cuando no es momento de arreglarla: se anota qué tiene y se sigue jugando.
//                   Anotar NO califica, no saltea y no saca la tarjeta de la ronda.
//
// La nota queda en la entrada (Entry.IssueNote) y aparece donde se la va a buscar después: la fila
// «Con un problema anotado» de la búsqueda, la ficha, y el filtro «Con problema» de la config.
// Vaciarla es darla por resuelta. Es una marca para ARREGLAR CONTENIDO, no un estado de estudio: no
// entra en la solidez ni cambia qué sale en una ronda.
public partial class PlaybookWindow
{
    private bool _issueOpen;     // el campo de "anotar un problema" está a la vista
    private long _issueEntryId;  // de qué entrada es lo que se está anotando
    // Dónde estaba el texto (la tarjeta del quiz o la ficha) antes de abrir el campo, si hubo que
    // subirlo para que no lo tapara (ver MakeRoomForIssue). null = no se lo movió.
    private (StackPanel Panel, VerticalAlignment Alignment, Thickness Margin)? _issueCardPlace;
    // Se editó, eliminó o anotó algo desde el quiz o la ficha: las filas de la búsqueda (conteos,
    // la fila de problemas) quedaron viejas y se rearman al volver a ella (ver BackToSearch).
    private bool _staleRows;

    private void InitActions()
    {
        QuizEditBtn.Click += (_, _) => EditQuizEntry();
        QuizDeleteBtn.Click += (_, _) => DeleteQuizEntry();
        QuizIssueBtn.Click += (_, _) => OpenIssue();
        FichaDeleteBtn.Click += (_, _) => DeleteFichaEntry();
        FichaIssueBtn.Click += (_, _) => OpenIssue();
        IssueSaveBtn.Click += (_, _) => SaveIssue();
        IssueCancelBtn.Click += (_, _) => CloseIssue();

        AutomationProperties.SetName(QuizEditBtn, Loc.T("Playbook.FichaEdit"));
        foreach (var button in new[] { QuizDeleteBtn, FichaDeleteBtn })
            AutomationProperties.SetName(button, Loc.T("Playbook.DeleteAccessible"));
        foreach (var button in new[] { QuizIssueBtn, FichaIssueBtn })
            AutomationProperties.SetName(button, Loc.T("Playbook.IssueAccessible"));
        AutomationProperties.SetName(IssueSaveBtn, Loc.T("Playbook.IssueSave"));
        AutomationProperties.SetName(IssueCancelBtn, Loc.T("Playbook.IssueCancel"));
    }

    /// <summary>La entrada sobre la que actúan E / X / R: la tarjeta del quiz o la ficha abierta.</summary>
    private Entry? ActionEntry => _stage switch
    {
        Stage.Quiz => CurrentCard?.Entry,
        Stage.Ficha when _fichaIndex >= 0 && _fichaIndex < _fichaEntries.Count => _fichaEntries[_fichaIndex],
        _ => null,
    };

    // ──────────────────────────── La foto en memoria ────────────────────────────
    // La ventana trabaja sobre una foto de la base (ver LoadData). Editar o eliminar UNA entrada no
    // justifica releer todo en medio de una ronda — y releer rearmaría el mazo —, así que la entrada
    // se cambia (o se saca) en cada lugar donde la foto la tiene. Están TODOS acá, en estos dos
    // métodos, para que ninguna operación pueda olvidarse uno y dejar una versión vieja jugando.

    private void AdoptEntry(Entry fresh)
    {
        List<Entry> Swap(IEnumerable<Entry> list) => list.Select(e => e.Id == fresh.Id ? fresh : e).ToList();

        _entries[fresh.Id] = fresh;
        _pickedEntries = Swap(_pickedEntries);
        _selection = Swap(_selection);
        _sideOptions = _sideOptions.Select(o => o with { Entries = Swap(o.Entries) }).ToList();
        if (_roundOption is { } option) _roundOption = option with { Entries = Swap(option.Entries) };
        _round = _round.Select(c => c.Entry.Id == fresh.Id ? c with { Entry = fresh } : c).ToList();
        _fichaEntries = Swap(_fichaEntries);
        _staleRows = true;
    }

    private void ForgetEntry(long id)
    {
        List<Entry> Without(IEnumerable<Entry> list) => list.Where(e => e.Id != id).ToList();

        _entries.Remove(id);
        _pickedEntries = Without(_pickedEntries);
        _selection = Without(_selection);
        _sideOptions = _sideOptions.Select(o => o with { Entries = Without(o.Entries) }).ToList();
        if (_roundOption is { } option) _roundOption = option with { Entries = Without(option.Entries) };
        _round = _round.Where(c => c.Entry.Id != id).ToList();
        _fichaEntries = Without(_fichaEntries);
        _staleRows = true;
    }

    // ──────────────────────────── E · editar (quiz) ────────────────────────────
    // (La edición desde la ficha ya existía: EditFichaEntry, en PlaybookWindow.Ficha.cs.)

    private void EditQuizEntry()
    {
        if (_stage != Stage.Quiz || _advancing || CurrentCard is not { } card) return;
        ClearGuard();
        long id = card.Entry.Id;

        // Se edita la versión de la BASE, no la foto de la ronda (mismo criterio que la ficha). Si ya
        // no existe —se la borró desde la config con la ronda abierta— sale de la ronda y se sigue.
        if (_store.GetEntry(id) is not { } current)
        {
            DropCurrentCard(id);
            return;
        }

        var outcome = KnowledgeEntryWindow.ShowEdit(this, _store, current);
        if (outcome.SavedCount == 0)
        {
            FocusCard(); // se canceló: no cambió nada, ni en la base ni en pantalla
            return;
        }
        if (_store.GetEntry(id) is not { } fresh)
        {
            DropCurrentCard(id);
            return;
        }

        AdoptEntry(fresh);
        // Con el texto pudieron cambiar sus traducciones (un término) o su glosario (una card): son
        // lo que muestran la respuesta y la ayuda.
        _translations = _store.GetTranslations(_pickedEntries.Select(e => e.Id));
        _glossaries = _store.GetAllGlossaries();

        // Si se le borró la traducción que se estaba mostrando como pregunta, ese lado ya no existe:
        // la tarjeta pasa a preguntarse por el frente, que siempre está.
        if (ReviewSide.TryGetLanguage(card.Side, out long languageId) && TranslationOf(id, languageId) is null)
            _round[_index] = _round[_index] with { Side = ReviewSide.Front };

        RefreshCurrentCard();
    }

    /// <summary>
    /// Repinta la tarjeta actual con lo que hay ahora en memoria, SIN cambiarle el estado: sigue en
    /// su lugar de la ronda y revelada (o no) como estaba. La ayuda se conserva abierta si todavía
    /// tiene qué mostrar.
    /// </summary>
    private void RefreshCurrentCard()
    {
        if (CurrentCard is not { } card) return;
        RenderCardHeader(card);
        if (_helperOpen && HelpStateOf(card) != HelpState.Available) _helperOpen = false;
        CenterCard();
        RenderCard(card);
        if (_revealed) CloseHelpIfAnswerDoesNotFit(card);
        ReserveHelpRoom(card);
        UpdateHelpControls();
        UpdateHint();
        FocusCard();
    }

    private void FocusCard()
    {
        CardScroll.Focus();
        Keyboard.Focus(CardScroll);
    }

    /// <summary>La entrada de la tarjeta actual ya no existe: sale de la ronda y sigue la que venía
    /// (o el resumen, si era la última). La posición no avanza: la siguiente ocupa su lugar.</summary>
    private void DropCurrentCard(long id)
    {
        ForgetEntry(id);
        if (_index >= _round.Count) EndRound();
        else ShowCard();
    }

    // ──────────────────────────── X · eliminar ────────────────────────────

    private static string DeleteNotice(Entry entry) =>
        Loc.T(entry.Kind == EntryKind.Term ? "Playbook.ConfirmDeleteTerm" : "Playbook.ConfirmDeleteCard");

    private void DeleteQuizEntry()
    {
        if (_stage != Stage.Quiz || _advancing || CurrentCard is not { } card) return;
        // Nunca un borrado mudo ni de una sola tecla: la X está entre la E y el Espacio.
        if (!ConfirmTwice(GuardKind.Delete, DeleteNotice(card.Entry), QuizDeleteBtn)) return;

        if (!_store.DeleteEntry(card.Entry.Id))
        {
            ShowNotice(Loc.T("Playbook.DeleteFailed"));
            return;
        }
        DropCurrentCard(card.Entry.Id);
    }

    private void DeleteFichaEntry()
    {
        if (_stage != Stage.Ficha || ActionEntry is not { } entry) return;
        if (!ConfirmTwice(GuardKind.Delete, DeleteNotice(entry), FichaDeleteBtn)) return;

        if (!_store.DeleteEntry(entry.Id))
        {
            ShowNotice(Loc.T("Playbook.DeleteFailed"));
            return;
        }
        // De vuelta a la búsqueda, releída: la entrada ya no está en ninguna lista.
        ForgetEntry(entry.Id);
        BackToSearch(reload: true);
    }

    // ──────────────────────────── R · anotar un problema ────────────────────────────

    private void ShowIssueMark(Border chip, Entry entry)
    {
        chip.Visibility = entry.HasIssue ? Visibility.Visible : Visibility.Collapsed;
        chip.ToolTip = entry.HasIssue ? entry.IssueNote : null;
        AutomationProperties.SetName(chip, entry.HasIssue
            ? string.Format(Loc.T("Playbook.IssueMarkAccessible"), entry.IssueNote)
            : "");
    }

    /// <summary>
    /// Abre el campo sobre la entrada actual, con la nota que ya tuviera: se la puede corregir, o
    /// vaciar para darla por resuelta. El panel es UNO y se muda al lugar de la vista que lo pide.
    /// </summary>
    private void OpenIssue()
    {
        if (_issueOpen || _advancing || ActionEntry is not { } entry) return;
        ClearGuard();

        var host = _stage == Stage.Ficha ? FichaIssueHost : QuizIssueHost;
        if (IssuePanel.Parent is Panel current && !ReferenceEquals(current, host))
        {
            current.Children.Remove(IssuePanel);
            host.Children.Add(IssuePanel);
        }

        _issueOpen = true;
        _issueEntryId = entry.Id;
        string label = Loc.T(entry.Kind == EntryKind.Term ? "Playbook.IssueQuestionTerm" : "Playbook.IssueQuestionCard");
        IssueLabel.Text = label;
        AutomationProperties.SetName(IssueBox, label);
        IssueBox.Text = entry.IssueNote;
        ShowIssueError("");
        IssuePanel.Visibility = Visibility.Visible;
        HintText.Text = Loc.T("Playbook.HintIssue");

        UpdateLayout(); // recién visible: sin layout, Focus() no prende
        if (_stage == Stage.Ficha) MakeRoomForIssue(FichaScroll, FichaPanel);
        else MakeRoomForIssue(CardScroll, CardPanel);
        IssueBox.Focus();
        Keyboard.Focus(IssueBox);
        IssueBox.CaretIndex = IssueBox.Text.Length;
    }

    /// <summary>
    /// Lo que se anota es SOBRE el texto de la entrada: no puede quedar tapado por el campo. Casi
    /// siempre no hace falta nada (el texto está centrado y termina antes de donde empieza el panel);
    /// si es tan alto que se pisarían, se lo sube lo indispensable mientras el campo está abierto, y
    /// al cerrarlo vuelve exactamente a donde estaba. Mismo criterio que la ayuda (ReserveHelpRoom).
    /// Si ni arriba del todo entra (una ficha muy cargada), lo que queda debajo del panel es su final
    /// — en la ficha, justamente la nota que se está editando.
    /// </summary>
    private void MakeRoomForIssue(ScrollViewer scroll, StackPanel text)
    {
        _issueCardPlace = null;
        if (scroll.ViewportHeight <= 0) return; // todavía sin medir
        // El alto que le queda a la vista al texto: desde el borde de arriba de su zona (el relleno
        // del scroller) hasta donde empieza el panel, con un respiro.
        double free = IssuePanel.TranslatePoint(new Point(0, 0), scroll).Y - scroll.Padding.Top - 12;
        double top = VisualTreeHelper.GetParent(text) is UIElement host
            ? text.TranslatePoint(new Point(0, 0), host).Y + scroll.VerticalOffset
            : 0;
        if (top + text.ActualHeight <= free) return;

        _issueCardPlace = (text, text.VerticalAlignment, text.Margin);
        text.VerticalAlignment = VerticalAlignment.Top;
        text.Margin = new Thickness(0, System.Math.Max(0, System.Math.Round(free - text.ActualHeight)), 0, 0);
        UpdateLayout();
    }

    private void RestoreCardAfterIssue()
    {
        if (_issueCardPlace is not { } place) return;
        _issueCardPlace = null;
        place.Panel.VerticalAlignment = place.Alignment;
        place.Panel.Margin = place.Margin;
    }

    /// <summary>El motivo de un guardado fallido reemplaza a la explicación del campo, en el mismo
    /// renglón (nada se mueve) y con la palabra adelante: no depende de un color.</summary>
    private void ShowIssueError(string text)
    {
        IssueError.Text = text;
        IssueError.Visibility = text == "" ? Visibility.Collapsed : Visibility.Visible;
        IssueHelpText.Visibility = text == "" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Las teclas con el campo abierto: Esc lo cierra SIN guardar (y nada más: no corta la
    /// ronda ni cierra la ventana), Ctrl+Enter guarda. Todo lo demás es del campo de texto.</summary>
    private void OnIssueKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseIssue();
        }
        else if (e.Key is Key.Enter or Key.Return && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            SaveIssue();
        }
    }

    private void SaveIssue()
    {
        if (!_issueOpen) return;
        if (!StoreIssue())
        {
            // No se guardó: se dice acá y lo escrito queda en el campo para reintentar.
            ShowIssueError(Loc.T("Playbook.IssueSaveFailed"));
            return;
        }
        CloseIssue();
    }

    /// <summary>Guarda lo que hay en el campo en SU entrada y actualiza la foto en memoria.</summary>
    private bool StoreIssue()
    {
        if (!_entries.TryGetValue(_issueEntryId, out var entry)) return true; // ya no existe: nada que anotar
        string note = IssueBox.Text.Trim();
        if (note == entry.IssueNote) return true;
        if (!_store.SetEntryIssue(entry.Id, note)) return false;
        // Se relee la entrada para tomar la fecha que le puso la base; si justo dejó de responder,
        // vale la nota que se acaba de guardar.
        AdoptEntry(_store.GetEntry(entry.Id) ?? entry with { IssueNote = note });
        return true;
    }

    private void CloseIssue()
    {
        if (!_issueOpen) return;
        _issueOpen = false;
        IssuePanel.Visibility = Visibility.Collapsed;
        RestoreCardAfterIssue();
        UpdateHint();

        // La marca (y, en la ficha, la nota escrita) reflejan lo que quedó.
        if (_stage == Stage.Quiz && CurrentCard is { } card)
        {
            ShowIssueMark(CardIssueChip, card.Entry);
            FocusCard();
        }
        else if (_stage == Stage.Ficha && _fichaIndex < _fichaEntries.Count)
        {
            RenderFicha();
        }
    }

    /// <summary>
    /// Se está por dejar la entrada (otra tarjeta, otro paso) con el campo todavía abierto — sólo
    /// puede pasar con el mouse, clickeando algo de atrás. Lo escrito se GUARDA en vez de tirarse:
    /// descartar es lo que hace Esc, a propósito; irse sin querer no puede costar la nota.
    /// </summary>
    private void CommitIssueIfOpen()
    {
        if (!_issueOpen) return;
        StoreIssue();
        _issueOpen = false;
        IssuePanel.Visibility = Visibility.Collapsed;
        // Viene otra tarjeta, que se ubica sola; la ficha en cambio conserva su panel: se repone.
        if (_issueCardPlace is { } place && ReferenceEquals(place.Panel, FichaPanel)) RestoreCardAfterIssue();
        _issueCardPlace = null;
    }
}

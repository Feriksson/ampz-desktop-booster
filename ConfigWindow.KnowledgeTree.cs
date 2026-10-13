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

// ── Pestaña CONOCIMIENTO · el ÁRBOL: plegar, reordenar y arrastrar ───────────────────────────────
//
// Tres pedidos del uso real, cuando las bibliotecas pasaron de un puñado a un árbol:
//
//   • PLEGAR. Con todo abierto la lista no entra en pantalla y encontrar una biblioteca es scrollear.
//     Cada biblioteca con hijas se pliega (el triángulo, o ← →) y lo recuerda: el estado vive en la
//     base (libraries.collapsed) y es el MISMO que usa la lista del playbooker.
//   • REORDENAR entre hermanas: Subir / Bajar, Alt+↑ ↓, o arrastrando al borde entre dos.
//   • ARRASTRAR una biblioteca sobre otra para meterla adentro.
//
// El árbol sigue siendo la lista plana con sangría (no un TreeView: ver ConfigWindow.Knowledge.cs).
//
// Regla que NO se negocia: el arrastre es un atajo, nunca el único camino. Todo lo que hace tiene su
// equivalente sin mouse — «Mover a…» para cambiar de madre, Subir / Bajar y Alt+↑ ↓ para el orden —
// y nada de lo que comunica depende sólo de un color: el destino se dice además con texto.
public partial class ConfigWindow
{
    private const string KnLibDragFormat = "AmpzDesktopBooster.KnowledgeLibraryDrag";

    /// <summary>Dónde caería lo que se arrastra respecto de la fila bajo el puntero.</summary>
    private enum KnDropZone { Inside, Before, After }

    // A cuánto del borde de la lista empieza a scrollear sola durante un arrastre.
    private const double KnDragScrollEdge = 22;

    private Point _knDragOrigin;
    private long? _knDragPressedId;      // la biblioteca sobre la que se apretó el botón (candidata a arrastre)
    private ListBoxItem? _knDropItem;    // la fila que hoy está pintada como destino
    private static readonly Brush KnDropLineBrush = Frozen(0x4F, 0xC3, 0xF7);
    private static readonly Brush KnDropFill = TranslucentAccent();

    private static Brush TranslucentAccent()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x70, 0x4F, 0xC3, 0xF7));
        brush.Freeze();
        return brush;
    }

    private void InitKnowledgeTree()
    {
        KnLibUpBtn.Click += (_, _) => ReorderKnLibrary(-1);
        KnLibDownBtn.Click += (_, _) => ReorderKnLibrary(1);
        KnLibExpandAllBtn.Click += (_, _) => SetAllKnCollapsed(false);
        KnLibCollapseAllBtn.Click += (_, _) => SetAllKnCollapsed(true);

        KnLibList.PreviewMouseLeftButtonDown += OnKnLibDragPress;
        KnLibList.PreviewMouseMove += OnKnLibDragMove;
        KnLibList.DragOver += OnKnLibDragOver;
        KnLibList.DragLeave += (_, _) => ClearKnDropFeedback();
        KnLibList.Drop += OnKnLibDrop;
    }

    // ──────────────────────────── Plegar / desplegar ────────────────────────────

    /// <summary>
    /// Despliega las bibliotecas plegadas que haya entre la raíz y <paramref name="libraryId"/>, para
    /// que quede a la vista. Devuelve si cambió algo (y entonces hay que releer).
    /// </summary>
    private bool ExpandKnAncestors(long libraryId, IReadOnlyList<Library> libraries)
    {
        var byId = libraries.ToDictionary(l => l.Id);
        bool changed = false;
        long? current = byId.TryGetValue(libraryId, out var self) ? self.ParentId : null;
        // Mismo tope anti-ciclo que LibraryTree: una base tocada a mano no puede girar para siempre.
        for (int steps = 0; current is { } id && steps <= byId.Count; steps++)
        {
            if (!byId.TryGetValue(id, out var ancestor)) break;
            if (ancestor.Collapsed && _knowledge.SetLibraryCollapsed(id, false)) changed = true;
            current = ancestor.ParentId;
        }
        return changed;
    }

    /// <summary>
    /// La biblioteca plegada MÁS ALTA de las que contienen a <paramref name="libraryId"/> (la que sí
    /// se ve en el árbol), o null si ninguna de arriba está plegada y por lo tanto ella misma se ve.
    /// </summary>
    private static long? KnOutermostCollapsedAncestor(long libraryId, IReadOnlyList<Library> libraries)
    {
        var byId = libraries.ToDictionary(l => l.Id);
        long? found = null;
        long? current = byId.TryGetValue(libraryId, out var self) ? self.ParentId : null;
        for (int steps = 0; current is { } id && steps <= byId.Count; steps++) // mismo tope anti-ciclo
        {
            if (!byId.TryGetValue(id, out var ancestor)) break;
            if (ancestor.Collapsed) found = id;
            current = ancestor.ParentId;
        }
        return found;
    }

    /// <summary>El triángulo de una fila con hijas; o un hueco del mismo ancho en una que no tiene.</summary>
    private FrameworkElement BuildKnDisclosure(Library library, bool hasChildren, bool collapsed)
    {
        // El hueco mide lo mismo que el botón con sus márgenes (ver el estilo KnDisclosure).
        if (!hasChildren) return new Border { Width = 24, Margin = new Thickness(-4, 0, 2, 0) };

        var button = new Button { Style = (Style)KnLibList.FindResource("KnDisclosure"), Content = collapsed ? "▸" : "▾" };
        AutomationProperties.SetName(button, string.Format(
            Loc.T(collapsed ? "Config.KnowledgeLibExpand" : "Config.KnowledgeLibCollapse"), library.Name));
        button.Click += (_, _) => SetKnCollapsed(library, !collapsed);
        return button;
    }

    private void SetKnCollapsed(Library library, bool collapsed)
    {
        if (!_knowledge.SetLibraryCollapsed(library.Id, collapsed))
        {
            ReportKnowledgeResult(KnowledgeOpResult.Failed, library.Name);
            return;
        }
        // Si lo elegido quedó ADENTRO de lo que se plegó, la selección pasa a la que se plegó: una
        // selección que no se ve es una acción (Eliminar, Mover) sobre algo que no se está mirando.
        // Lo resuelve el refresco (sin pedirle una biblioteca, conserva la elegida o sube a la
        // plegada que la contiene).
        RefreshKnowledge();
        FocusKnSelectedLibrary();
    }

    private void SetAllKnCollapsed(bool collapsed)
    {
        if (!_knowledge.SetAllLibrariesCollapsed(collapsed))
        {
            ReportKnowledgeResult(KnowledgeOpResult.Failed, "");
            return;
        }
        // Al contraer todo queda elegida la raíz de donde se estaba (lo demás ya no se ve): lo
        // resuelve el refresco, igual que al plegar una sola.
        RefreshKnowledge();
        FocusKnSelectedLibrary();
    }

    private void FocusKnSelectedLibrary()
    {
        if (KnLibList.SelectedItem is not ListBoxItem item) { KnLibList.Focus(); return; }
        KnLibList.ScrollIntoView(item);
        KnLibList.UpdateLayout();
        item.Focus();
    }

    private void SelectKnLibrary(long id)
    {
        var item = KnLibList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is Library l && l.Id == id);
        if (item is null) return;
        KnLibList.SelectedItem = item;
        FocusKnSelectedLibrary();
    }

    /// <summary>
    /// Las teclas del árbol, con el foco en la lista. ← pliega (o, si ya está plegada o no tiene
    /// hijas, sube a la madre); → despliega (o, si ya está abierta, baja a la primera hija): lo que
    /// hace cualquier árbol. Alt+↑ / Alt+↓ la suben o bajan entre sus hermanas.
    /// </summary>
    private void OnKnTreeKeyDown(KeyEventArgs e)
    {
        if (SelectedKnLibrary is not { } library) return;

        // Con Alt apretado WPF entrega Key.System y la tecla real viene en SystemKey.
        if (e.Key == Key.System && e.SystemKey is Key.Up or Key.Down)
        {
            e.Handled = true;
            ReorderKnLibrary(e.SystemKey == Key.Up ? -1 : 1);
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        var children = _knChildren[library.Id].OrderBy(l => l.Sort).ThenBy(l => l.Id).ToList();
        bool open = children.Count > 0 && !library.Collapsed;
        if (e.Key == Key.Left)
        {
            e.Handled = true;
            if (open) SetKnCollapsed(library, true);
            else if (library.ParentId is { } parent) SelectKnLibrary(parent);
        }
        else if (e.Key == Key.Right)
        {
            e.Handled = true;
            if (children.Count == 0) return;
            if (!open) SetKnCollapsed(library, false);
            else SelectKnLibrary(children[0].Id);
        }
    }

    // ──────────────────────────── Reordenar ────────────────────────────

    private List<Library> KnSiblings(Library library) =>
        _knChildren[library.ParentId].OrderBy(l => l.Sort).ThenBy(l => l.Id).ToList();

    /// <summary>Subir / Bajar: habilitados si hay adónde, y si no, POR QUÉ, escrito debajo.</summary>
    private void UpdateKnOrderButtons()
    {
        var library = SelectedKnLibrary;
        if (library is null)
        {
            KnLibUpBtn.IsEnabled = KnLibDownBtn.IsEnabled = false;
            KnLibOrderReason.Text = ""; // "elegí una biblioteca" ya lo dice KnLibReason
            return;
        }

        var siblings = KnSiblings(library);
        int index = siblings.FindIndex(l => l.Id == library.Id);
        KnLibUpBtn.IsEnabled = index > 0;
        KnLibDownBtn.IsEnabled = index >= 0 && index < siblings.Count - 1;
        KnLibOrderReason.Text = Loc.T(
            siblings.Count <= 1 ? "Config.KnowledgeLibOrderOnly"
            : index == 0 ? "Config.KnowledgeLibOrderFirst"
            : index == siblings.Count - 1 ? "Config.KnowledgeLibOrderLast"
            : "Config.KnowledgeLibOrderKeys");
    }

    private void ReorderKnLibrary(int delta)
    {
        if (SelectedKnLibrary is not { } library) return;
        var result = _knowledge.ReorderLibrary(library.Id, delta);
        // En una punta no hay adónde ir: no es un error (el motivo ya está escrito bajo los botones).
        if (result == KnowledgeOpResult.SameTarget) return;
        if (!ReportKnowledgeResult(result, library.Name, library: true)) return;
        RefreshKnowledge(selectLibraryId: library.Id);
        FocusKnSelectedLibrary();
    }

    // ──────────────────────────── Arrastrar ────────────────────────────
    // Mismo molde que el arrastre de variables de la pestaña Espacios (OnVarDragPress / OnVarDragMove):
    // umbral del sistema para no arrancar con un click tembloroso, y el payload como STRING (la capa
    // OLE serializa lo que viaja, y un tipo propio explotaría recién al soltar).

    private void OnKnLibDragPress(object sender, MouseButtonEventArgs e)
    {
        _knDragOrigin = e.GetPosition(null);
        var source = e.OriginalSource as DependencyObject;
        // Apretar sobre el triángulo es plegar, y sobre la barra de scroll es scrollear: ninguno de
        // los dos puede terminar arrastrando la biblioteca.
        _knDragPressedId = KnHasAncestor<System.Windows.Controls.Primitives.ButtonBase>(source)
                           || KnHasAncestor<System.Windows.Controls.Primitives.ScrollBar>(source)
            ? null
            : (ItemUnder(KnLibList, source)?.Tag as Library)?.Id;
    }

    private void OnKnLibDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _knDragPressedId is not { } id)
        {
            _knDragPressedId = null;
            return;
        }

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _knDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _knDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _knDragPressedId = null;
        DragDrop.DoDragDrop(KnLibList, new DataObject(KnLibDragFormat, id.ToString(CultureInfo.InvariantCulture)),
            DragDropEffects.Move);
        ClearKnDropFeedback(); // el drop pudo caer fuera: nada puede quedar pintado
    }

    private static bool KnHasAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>
    /// Qué pasaría si se soltara acá: bajo qué madre y en qué lugar entre sus hijas. O por qué no se
    /// puede (<paramref name="refusal"/>), con las mismas dos reglas que aplica el store —ciclo y
    /// nombre repetido entre hermanas— dichas ANTES de soltar. <c>null</c> = ahí no cambia nada.
    /// </summary>
    private (long? ParentId, int Index, string Hint)? KnDropPlan(long draggedId, ListBoxItem? item, KnDropZone zone, out string? refusal)
    {
        refusal = null;
        if (!_knLibraries.TryGetValue(draggedId, out var dragged)) return null;

        long? parentId;
        int index;
        string hint;
        if (item?.Tag is not Library target)
        {
            // El hueco de abajo de la lista: a la raíz, al final.
            parentId = null;
            index = int.MaxValue;
            hint = Loc.T("Config.KnowledgeDropRoot");
        }
        else if (target.Id == draggedId)
        {
            return null; // sobre sí misma: no es un destino
        }
        else if (zone == KnDropZone.Inside)
        {
            parentId = target.Id;
            index = int.MaxValue;
            hint = string.Format(Loc.T("Config.KnowledgeDropInside"), target.Name);
        }
        else
        {
            parentId = target.ParentId;
            // La posición FINAL se cuenta sin la arrastrada (ver KnowledgeStore.PlaceLibrary).
            var siblings = _knChildren[parentId].Where(l => l.Id != draggedId).OrderBy(l => l.Sort).ThenBy(l => l.Id).ToList();
            index = siblings.FindIndex(l => l.Id == target.Id) + (zone == KnDropZone.After ? 1 : 0);
            hint = string.Format(Loc.T(zone == KnDropZone.Before ? "Config.KnowledgeDropBefore" : "Config.KnowledgeDropAfter"), target.Name);
        }

        if (parentId is { } p && KnSubtreeIds(draggedId).Contains(p))
        {
            refusal = Loc.T("Config.KnowledgeDropCycle");
            return null;
        }
        if (parentId != dragged.ParentId
            && _knChildren[parentId].Any(l => l.Id != draggedId && LibraryTree.SameSiblingName(l.Name, dragged.Name)))
        {
            refusal = string.Format(Loc.T("Config.KnowledgeDropNameTaken"), dragged.Name);
            return null;
        }
        if (parentId == dragged.ParentId)
        {
            // Mismo nivel y mismo lugar (soltarla pegada a sí misma): no hay nada que hacer.
            var all = KnSiblings(dragged);
            int from = all.FindIndex(l => l.Id == draggedId);
            if (Math.Clamp(index, 0, all.Count - 1) == from) return null;
        }
        return (parentId, index, hint);
    }

    private (ListBoxItem? Item, KnDropZone Zone) KnDropTargetAt(DragEventArgs e)
    {
        var item = ItemUnder(KnLibList, ItemHitTest(KnLibList, e));
        if (item is null) return (null, KnDropZone.Inside);
        // El cuarto de arriba y el de abajo de la fila son "entre"; el medio, "adentro".
        double y = e.GetPosition(item).Y, height = Math.Max(1, item.ActualHeight);
        return (item, y < height * 0.28 ? KnDropZone.Before : y > height * 0.72 ? KnDropZone.After : KnDropZone.Inside);
    }

    private static long? KnDraggedId(DragEventArgs e) =>
        e.Data.GetData(KnLibDragFormat) is string payload
        && long.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : null;

    private void OnKnLibDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (KnDraggedId(e) is not { } draggedId) return; // otro arrastre (variables, archivos): acá no cae

        var (item, zone) = KnDropTargetAt(e);
        var plan = KnDropPlan(draggedId, item, zone, out string? refusal);
        e.Effects = plan is null ? DragDropEffects.None : DragDropEffects.Move;

        // Tres estados, cada uno con su dibujo Y su texto: adentro (la fila se rellena), entre (una
        // línea en el borde), no se puede (el cursor de prohibido de Windows + el motivo escrito).
        ShowKnDropFeedback(plan is null ? null : item, zone);
        KnLibOrderReason.Text = plan?.Hint ?? refusal ?? "";

        KnAutoScroll(e.GetPosition(KnLibList).Y);
    }

    private void OnKnLibDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var (item, zone) = KnDropTargetAt(e);
        ClearKnDropFeedback();
        if (KnDraggedId(e) is not { } draggedId || !_knLibraries.TryGetValue(draggedId, out var dragged)) return;
        if (KnDropPlan(draggedId, item, zone, out _) is not { } plan) return;

        var result = _knowledge.PlaceLibrary(draggedId, plan.ParentId, plan.Index);
        if (result != KnowledgeOpResult.SameTarget && !ReportKnowledgeResult(result, dragged.Name, library: true)) return;
        // El refresco despliega el camino hasta ella (ExpandKnAncestors): soltada dentro de una
        // biblioteca plegada, se ve dónde quedó.
        RefreshKnowledge(selectLibraryId: draggedId);
        FocusKnSelectedLibrary();
    }

    /// <summary>Cerca del borde de arriba o de abajo la lista scrollea sola, para llegar a un destino
    /// que no está a la vista sin soltar lo que se lleva.</summary>
    private void KnAutoScroll(double y)
    {
        if (KnFindDescendant<ScrollViewer>(KnLibList) is not { } scroller) return;
        if (y < KnDragScrollEdge) scroller.LineUp();
        else if (y > KnLibList.ActualHeight - KnDragScrollEdge) scroller.LineDown();
    }

    private static T? KnFindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (KnFindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // ── Lo que se pinta durante el arrastre ──
    // El contenido de cada fila es una grilla con tres capas fijas: [0] el relleno de "cae adentro",
    // [1] la línea de arriba y [2] la de abajo ("cae entre"), y encima el contenido. Las líneas van
    // superpuestas al borde (no en una fila propia): mostrarlas no corre nada.

    private static Grid WrapKnDropVisuals(UIElement content)
    {
        var grid = new Grid();
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(-6, -3, -6, -3), IsHitTestVisible = false });
        grid.Children.Add(KnDropLine(VerticalAlignment.Top));
        grid.Children.Add(KnDropLine(VerticalAlignment.Bottom));
        grid.Children.Add(content);
        return grid;
    }

    private static Border KnDropLine(VerticalAlignment edge) => new()
    {
        Height = 3,
        VerticalAlignment = edge,
        Margin = edge == VerticalAlignment.Top ? new Thickness(-6, -7, -6, 0) : new Thickness(-6, 0, -6, -7),
        CornerRadius = new CornerRadius(1.5),
        Background = KnDropLineBrush,
        Visibility = Visibility.Hidden,
        IsHitTestVisible = false,
    };

    private void ShowKnDropFeedback(ListBoxItem? item, KnDropZone zone)
    {
        if (!ReferenceEquals(_knDropItem, item)) PaintKnDrop(_knDropItem, null);
        _knDropItem = item;
        PaintKnDrop(item, zone);
    }

    private void ClearKnDropFeedback()
    {
        PaintKnDrop(_knDropItem, null);
        _knDropItem = null;
        UpdateKnOrderButtons(); // devuelve el texto de Subir / Bajar que el arrastre había reemplazado
    }

    private static void PaintKnDrop(ListBoxItem? item, KnDropZone? zone)
    {
        if (item?.Content is not Grid { Children.Count: >= 3 } grid) return;
        // Acento TRANSLÚCIDO para "adentro" (mismo que el arrastre de variables): la fila se sigue leyendo.
        ((Border)grid.Children[0]).Background = zone == KnDropZone.Inside ? KnDropFill : Brushes.Transparent;
        grid.Children[1].Visibility = zone == KnDropZone.Before ? Visibility.Visible : Visibility.Hidden;
        grid.Children[2].Visibility = zone == KnDropZone.After ? Visibility.Visible : Visibility.Hidden;
    }
}

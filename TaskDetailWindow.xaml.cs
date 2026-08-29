using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using AmpzDesktopBooster.Services.Tasks;

namespace AmpzDesktopBooster;

/// <summary>
/// Mini-panel de detalle de la tarea activa (click en el widget de la barra). Muestra el texto
/// completo (en la barra va recortado), el identifier y el proyecto, y ofrece tres acciones:
///   · Abrir tarea  → abre TaskItem.Url en el browser (oculto si la tarea no trae URL).
///   · Elegir otra  → cierra y reabre el picker (callback al router).
///   · Desanclar    → limpia la tarea de la sesión y oculta el widget (callback al router).
///
/// Se posiciona abajo-derecha, JUSTO arriba de la barra (el widget vive a la derecha). Como toda
/// ventana utilitaria, se abre con ShowFocused → entra en la red anti-hook/foco-huérfano al cerrar.
/// </summary>
public partial class TaskDetailWindow : Window
{
    private readonly TaskItem _task;
    private readonly Action _onPickAnother;
    private readonly Action _onUnpin;

    public TaskDetailWindow(TaskItem task, Action onPickAnother, Action onUnpin)
    {
        InitializeComponent();

        _task = task;
        _onPickAnother = onPickAnother;
        _onUnpin = onUnpin;

        FitToScreen();

        IdText.Text = task.Identifier;
        IdText.Visibility = string.IsNullOrEmpty(task.Identifier) ? Visibility.Collapsed : Visibility.Visible;
        ProjectText.Text = string.IsNullOrEmpty(task.Project) ? "" : task.Project;
        TitleText.Text = task.Title;

        // Body / descripción: si no viene (Trello sin desc, Vikunja sin description) el panel entero
        // se Colapsa para no dejar una caja vacía.
        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            BodyText.Text = task.Description;
            BodyPanel.Visibility = Visibility.Visible;
        }

        // "Abrir tarea" sólo tiene sentido si la tarea trae URL.
        OpenBtn.IsEnabled = !string.IsNullOrWhiteSpace(task.Url);

        OpenBtn.Click  += (_, _) => OpenUrl();
        PickBtn.Click  += (_, _) => { Close(); _onPickAnother(); };
        UnpinBtn.Click += (_, _) => { _onUnpin(); Close(); };

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // Mismo flyout-behavior que el picker: click afuera o cambio de desktop lo cierra.
        this.CloseOnDeactivate();

        Loaded += (_, _) => PositionAboveBar();
    }

    /// <summary>
    /// Ancho de la ventana y techo del panel de descripción, dimensionados contra el WorkArea.
    ///
    /// Mismo criterio que el picker: la ventana es NoResize, así que un ancho fijo que no entre en
    /// pantalla no se puede corregir arrastrando. Los valores del XAML (820 / 600) son el tamaño al
    /// que aspiramos; acá se recortan si el monitor no da.
    ///
    /// El alto NO se setea: con SizeToContent="Height" la ventana crece con el contenido. Lo que
    /// acotamos es el TECHO del scroller de la descripción — sin él, una tarea con un body enorme
    /// haría una ventana más alta que la pantalla, y como se ancla por el borde INFERIOR
    /// (PositionAboveBar), lo que se saldría de cuadro es el título. El 0.55 deja lugar para el
    /// título, los botones y los hints.
    /// </summary>
    private void FitToScreen()
    {
        var wa = SystemParameters.WorkArea;
        Width = Math.Max(460, Math.Min(820, wa.Width - 40));
        BodyScroller.MaxHeight = Math.Max(260, Math.Min(600, wa.Height * 0.55));
    }

    /// <summary>Pega la ventana abajo-derecha, arriba de la barra. WorkArea ya excluye el alto de la
    /// AppBar registrada, así que su borde inferior cae justo encima de la barra.</summary>
    private void PositionAboveBar()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Bottom - ActualHeight - 8;
    }

    private void OpenUrl()
    {
        if (string.IsNullOrWhiteSpace(_task.Url))
            return;
        try
        {
            // UseShellExecute=true → abre con el browser por defecto del usuario.
            Process.Start(new ProcessStartInfo(_task.Url) { UseShellExecute = true });
        }
        catch
        {
            // URL inválida / sin browser asociado → no tumbamos la app por abrir un link.
        }
        Close();
    }
}

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

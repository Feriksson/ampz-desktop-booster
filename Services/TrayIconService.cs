using System.Drawing;
using System.Windows.Forms;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// Maneja el ícono en la bandeja del sistema (system tray) y su menú contextual.
/// Usa System.Windows.Forms.NotifyIcon (viene en el SDK — CERO NuGet).
/// No conoce WPF: se comunica hacia afuera con callbacks. Desacople puro.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _generatedIcon;

    // Etiqueta legible por widget para el submenú. Orden = orden del menú.
    // "Fecha" no está: el día + fecha viajan SIEMPRE con la hora (no es un toggle aparte).
    private static readonly (WidgetKind Kind, string Label)[] WidgetMenu =
    {
        (WidgetKind.Clock,   "Hora y fecha"),
        (WidgetKind.Cpu,     "CPU"),
        (WidgetKind.Ram,     "RAM"),
        (WidgetKind.Network, "Red"),
        (WidgetKind.Battery, "Batería"),
    };

    /// <param name="settings">Estado actual de los widgets (para marcar los checks).</param>
    /// <param name="onToggle">Se dispara al prender/apagar un widget.</param>
    public TrayIconService(
        WidgetSettings settings,
        Action onExit,
        Action onReposition,
        Action<WidgetKind, bool> onToggle,
        Action onOpenConfig,
        bool autoStartEnabled,
        Action<bool> onToggleAutoStart)
    {
        var menu = new ContextMenuStrip();

        var header = new ToolStripMenuItem("Ampz Desktop Booster") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Configuración", null, (_, _) => onOpenConfig());
        menu.Items.Add(new ToolStripSeparator());

        // ----- Submenú "Widgets" con un toggle por cada uno -----
        var widgetsRoot = new ToolStripMenuItem("Widgets");
        foreach (var (kind, label) in WidgetMenu)
        {
            var item = new ToolStripMenuItem(label)
            {
                CheckOnClick = true,           // el click alterna el check solo
                Checked = settings.Get(kind),  // refleja el estado persistido
                Tag = kind,                    // guardamos el enum para el handler
            };
            item.CheckedChanged += (s, _) =>
            {
                var mi = (ToolStripMenuItem)s!;
                onToggle((WidgetKind)mi.Tag!, mi.Checked);
            };
            widgetsRoot.DropDownItems.Add(item);
        }
        menu.Items.Add(widgetsRoot);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reposicionar barra", null, (_, _) => onReposition());

        // Toggle "iniciar con Windows": el check refleja la clave Run; el click la escribe/borra.
        var autoStart = new ToolStripMenuItem("Iniciar con Windows")
        {
            CheckOnClick = true,
            Checked = autoStartEnabled,
        };
        autoStart.CheckedChanged += (s, _) => onToggleAutoStart(((ToolStripMenuItem)s!).Checked);
        menu.Items.Add(autoStart);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => onExit());

        // Ícono real de la app (embebido en el .exe). Si falla, caemos al dibujado a mano.
        _generatedIcon = AppIcon.TryLoadForTray() ?? CreateIcon();

        _icon = new NotifyIcon
        {
            Text = "Ampz Desktop Booster",
            Visible = true,
            Icon = _generatedIcon,
            ContextMenuStrip = menu,
        };

        // Doble click en el ícono también reposiciona (atajo cómodo).
        _icon.DoubleClick += (_, _) => onReposition();
    }

    /// <summary>Actualiza el tooltip del ícono con métricas en vivo (máx 63 chars).</summary>
    public void SetTooltip(string text)
    {
        // NotifyIcon.Text revienta si pasás de 63 caracteres. Lo recortamos defensivamente.
        _icon.Text = text.Length <= 63 ? text : text[..63];
    }

    /// <summary>
    /// FALLBACK: genera el ícono a mano (un mini-mockup de la barra) sólo si no se pudo
    /// extraer el .ico real del exe. En condiciones normales no se usa.
    /// </summary>
    private static Icon CreateIcon()
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using var screen = new SolidBrush(Color.FromArgb(0x80, 0x9A, 0x9A, 0x9A));
                g.FillRectangle(screen, 4, 5, 24, 16); // "pantalla"

                using var bar = new SolidBrush(Color.FromArgb(0xFF, 0x4F, 0xC3, 0xF7));
                g.FillRectangle(bar, 4, 23, 24, 5);    // la barra (accent)
            }

            // GetHicon crea un HICON nativo; Icon.FromHandle lo envuelve.
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return SystemIcons.Application; // fallback si algo del dibujo falla
        }
    }

    public void Dispose()
    {
        _icon.Visible = false; // si no, el ícono queda fantasma hasta que pasás el mouse
        _icon.Dispose();
        _generatedIcon.Dispose();
    }
}

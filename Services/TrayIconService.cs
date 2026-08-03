using System.Drawing;
using System.Windows.Forms;
using AmpzDesktopBooster.Services.Localization;

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
    // Las labels se resuelven en tiempo de construcción del menú (no es estático) para respetar el idioma activo.
    private static (WidgetKind Kind, string LabelKey)[] WidgetMenuKeys =
    {
        (WidgetKind.Clock,   "Tray.WidgetClock"),
        (WidgetKind.Cpu,     "Tray.WidgetCpu"),
        (WidgetKind.Ram,     "Tray.WidgetRam"),
        (WidgetKind.Network, "Tray.WidgetNetwork"),
        (WidgetKind.Ip,      "Tray.WidgetIp"),
        (WidgetKind.Battery, "Tray.WidgetBattery"),
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

        menu.Items.Add(Loc.T("Tray.Settings"), null, (_, _) => onOpenConfig());
        menu.Items.Add(new ToolStripSeparator());

        // ----- Submenú "Widgets" con un toggle por cada uno -----
        var widgetsRoot = new ToolStripMenuItem(Loc.T("Tray.Widgets"));
        foreach (var (kind, labelKey) in WidgetMenuKeys)
        {
            var item = new ToolStripMenuItem(Loc.T(labelKey))
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
        menu.Items.Add(Loc.T("Tray.RepositionBar"), null, (_, _) => onReposition());

        // Toggle "iniciar con Windows": el check refleja la clave Run; el click la escribe/borra.
        var autoStart = new ToolStripMenuItem(Loc.T("Tray.StartWithWindows"))
        {
            CheckOnClick = true,
            Checked = autoStartEnabled,
        };
        autoStart.CheckedChanged += (s, _) => onToggleAutoStart(((ToolStripMenuItem)s!).Checked);
        menu.Items.Add(autoStart);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("Tray.Quit"), null, (_, _) => onExit());

        // Ícono real de la app (embebido en el .exe). Si falla, caemos al dibujado a mano.
        _generatedIcon = AppIcon.TryLoadForTray() ?? CreateIcon();

        _icon = new NotifyIcon
        {
            Text = "Ampz Desktop Booster",
            Visible = true,
            Icon = _generatedIcon,
            ContextMenuStrip = menu,
        };

        // Doble click en el ícono → CONFIGURACIÓN. Antes reposicionaba la barra, pero reposicionar
        // es una acción de rescate que se usa cada muerte de obispo; la config es a donde de verdad
        // vas cuando le apuntás al ícono. El doble click es el gesto más accesible del tray: se lo
        // queda la acción frecuente, no la excepcional. "Reposicionar barra" no se pierde — sigue
        // en el menú contextual, que es un click derecho de distancia.
        _icon.DoubleClick += (_, _) => onOpenConfig();
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

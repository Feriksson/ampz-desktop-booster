using System;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// Carga el ícono de la app ("ampz desktop booster - icon.ico"). Lo leemos del .exe, donde
/// quedó EMBEBIDO por &lt;ApplicationIcon&gt; en el .csproj — así no dependemos de que el .ico
/// suelto esté copiado junto al binario. Lo exponemos en los dos formatos que la app necesita:
///   - System.Drawing.Icon  → para el NotifyIcon del system tray (WinForms)
///   - ImageSource          → para el chrome de las ventanas WPF (taskbar / Alt-Tab / título)
/// </summary>
internal static class AppIcon
{
    /// <summary>Ícono para el tray. El caller es dueño de disponerlo. null si no se pudo extraer.</summary>
    public static Icon? TryLoadForTray()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                return Icon.ExtractAssociatedIcon(exe);
        }
        catch { /* sin acceso al exe → el caller cae a su fallback */ }
        return null;
    }

    /// <summary>Ícono para una ventana WPF (Window.Icon). null si no se pudo cargar.</summary>
    public static ImageSource? TryLoadForWindow()
    {
        try
        {
            using var icon = TryLoadForTray();
            if (icon is not null)
            {
                // CreateBitmapSourceFromHIcon copia los pixeles → seguro disponer el Icon después.
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze(); // cross-thread safe + un toque más liviano
                return src;
            }
        }
        catch { /* degradamos al ícono default de WPF */ }
        return null;
    }
}

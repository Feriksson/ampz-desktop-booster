using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmpzDesktopBooster.Services.Localization;
using QRCoder;

namespace AmpzDesktopBooster;

/// <summary>
/// Popup mínimo que muestra el QR de una URL (la URL-de-red de un servicio local) + la URL en texto.
/// Sin XAML propio: es un diálogo chico y estático, no vale un par de archivos. Escaneás con el celu
/// y entrás al servicio sin tipear la IP a mano.
///
/// El QR se genera con QRCoder → <see cref="PngByteQRCode"/> (bytes PNG) → <see cref="BitmapImage"/>.
/// Esa vía NO usa System.Drawing (que en WPF chocaría con System.Windows.Media). Best-effort: si la
/// generación falla por lo que sea, mostramos solo la URL (nunca crasheamos por un QR).
/// </summary>
public sealed class QrWindow : Window
{
    public QrWindow(string url, string caption)
    {
        Title = caption;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        FontFamily = new FontFamily("Segoe UI");

        var panel = new StackPanel { Margin = new Thickness(18) };

        panel.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12),
            TextAlignment = TextAlignment.Center,
        });

        var img = BuildQr(url);
        if (img is not null)
        {
            // Fondo blanco detrás del QR: los lectores necesitan contraste alto (módulos negros sobre
            // claro). Sobre el fondo oscuro de la app un QR "transparente" no escanearía bien.
            panel.Children.Add(new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Image { Source = img, Width = 240, Height = 240 },
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = url,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
            FontSize = 13,
            Margin = new Thickness(0, 12, 0, 0),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Services.QrHint"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x78)),
            FontSize = 10,
            Margin = new Thickness(0, 10, 0, 0),
            TextAlignment = TextAlignment.Center,
        });

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = panel,
        };

        // Cualquier tecla o clic cierra: es un visor efímero, no hay nada que operar acá.
        KeyDown += (_, _) => Close();
        MouseDown += (_, _) => Close();
    }

    private static BitmapImage? BuildQr(string url)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data);
            byte[] bytes = png.GetGraphic(10); // 10 px por módulo → nítido a 240px

            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // materializa ya: el stream se cierra al salir
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // sin QR: la ventana igual muestra la URL en texto
        }
    }
}

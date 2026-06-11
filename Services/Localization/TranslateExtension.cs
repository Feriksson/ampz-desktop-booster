using System;
using System.Windows.Markup;

namespace AmpzDesktopBooster.Services.Localization;

/// <summary>
/// Markup extension para traducir desde XAML: <c>Text="{loc:Translate General.Title}"</c>.
/// Resuelve la key contra <see cref="Loc"/> en tiempo de carga de la ventana. Como el modelo es por
/// REINICIO (no hot-reload), resolver una sola vez al construir la ventana alcanza: el idioma activo
/// ya está fijado por <see cref="Loc.Init"/> antes de que cualquier ventana se monte.
///
/// Uso en XAML (declarar el namespace una vez por ventana):
///   xmlns:loc="clr-namespace:AmpzDesktopBooster.Services.Localization"
///   Text="{loc:Translate Key=General.Title}"   o   Text="{loc:Translate General.Title}"
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TranslateExtension : MarkupExtension
{
    /// <summary>La key del diccionario a resolver (ej. "General.Title").</summary>
    public string Key { get; set; } = "";

    public TranslateExtension() { }

    /// <summary>Permite la forma posicional: <c>{loc:Translate General.Title}</c>.</summary>
    public TranslateExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}

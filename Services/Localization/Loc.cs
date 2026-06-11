using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace AmpzDesktopBooster.Services.Localization;

/// <summary>
/// Servicio central de localización (i18n). Mantiene el idioma activo y un diccionario plano
/// <c>key → texto</c> cargado del JSON embebido del idioma elegido (<c>Strings.es.json</c> /
/// <c>Strings.en.json</c>, embebidos como Resource — mismo patrón que los logos de Providers).
///
/// Modelo de aplicación: por REINICIO, no en caliente (decisión del proyecto). <see cref="Init"/>
/// corre UNA vez al arranque, ANTES de montar cualquier ventana; los textos se resuelven al construir
/// cada ventana (casi todas son efímeras: se recrean al abrirlas por hotkey, así que ya salen en el
/// idioma activo). <see cref="SetAndPersist"/> cambia la preferencia para el PRÓXIMO arranque.
///
/// La markup extension <see cref="TranslateExtension"/> es el acceso desde XAML; <see cref="T"/> el
/// acceso desde code-behind. Una key sin traducir devuelve la propia key (se nota en la UI, no rompe).
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    /// <summary>Idioma activo en esta corrida. Se fija en <see cref="Init"/> y no cambia hasta reiniciar.</summary>
    public static AppLanguage Current { get; private set; } = AppLanguage.Spanish;

    /// <summary>
    /// Carga la preferencia persistida y su diccionario. Llamar UNA vez en <c>App.OnStartup</c>, antes
    /// de crear ventanas. Si algo falla, queda el diccionario vacío → la UI muestra las keys (visible
    /// pero no fatal), nunca crashea.
    /// </summary>
    public static void Init()
    {
        Current = LanguageSettings.Load().Language;
        LoadDictionary(Current);
    }

    /// <summary>
    /// Persiste el nuevo idioma para el próximo arranque y recarga el diccionario en memoria (por si
    /// algún texto se resuelve antes de reiniciar). El cambio VISIBLE pleno llega al reiniciar la app.
    /// </summary>
    public static void SetAndPersist(AppLanguage lang)
    {
        new LanguageSettings { Language = lang }.Save();
        Current = lang;
        LoadDictionary(lang);
    }

    /// <summary>Traduce una key al idioma activo. Sin entrada → devuelve la key (fallback visible).</summary>
    public static string T(string key) =>
        _strings.TryGetValue(key, out var value) ? value : key;

    private static void LoadDictionary(AppLanguage lang)
    {
        string file = lang == AppLanguage.English ? "Strings.en.json" : "Strings.es.json";
        try
        {
            var uri = new Uri($"/Localization/{file}", UriKind.Relative);
            var info = Application.GetResourceStream(uri);
            if (info is null) { _strings = new(StringComparer.Ordinal); return; }

            using var stream = info.Stream;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            _strings = loaded ?? new(StringComparer.Ordinal);
        }
        catch
        {
            // Recurso ausente/JSON inválido → diccionario vacío. La UI cae a las keys, no crashea.
            _strings = new(StringComparer.Ordinal);
        }
    }
}

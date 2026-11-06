using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster.Credentials;

/// <summary>
/// Textos de UI de los vínculos OAuth, en UN lugar: el gestor, el editor y el picker describen la
/// misma cuenta vinculada, y tres formatos distintos para lo mismo harían dudar si es la misma.
/// </summary>
public static class CredentialLabels
{
    /// <summary>
    /// Etiqueta del proveedor. Las marcas NO se traducen (son nombres propios, y "Continuar con
    /// Google" dice Google en todos los idiomas); sólo "Ninguno" y "Otro" pasan por <see cref="Loc"/>.
    /// </summary>
    public static string Provider(CredentialProvider p) => p switch
    {
        CredentialProvider.None => Loc.T("Creds.ProviderNone"),
        CredentialProvider.Other => Loc.T("Creds.ProviderOther"),
        _ => p.ToString(),
    };

    /// <summary>Badge de la lista/detalle: vacío para una cuenta que no es de identidad.</summary>
    public static string Badge(Credential c) => c.Provider == CredentialProvider.None ? "" : Provider(c.Provider);

    /// <summary>
    /// "Proveedor · dónde vive" de una cuenta vinculada (el título va aparte, con más peso). El
    /// "dónde" es obligatorio: los vínculos cruzan espacios, y "Gmail" a secas no dice si es la de
    /// Synxs o la personal.
    /// </summary>
    public static string Where(string scopeKey, Credential c)
    {
        string badge = Badge(c);
        string scope = ScopePicker.LabelOf(scopeKey);
        return badge == "" ? scope : $"{badge} · {scope}";
    }
}

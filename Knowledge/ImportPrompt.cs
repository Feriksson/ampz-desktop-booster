using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace AmpzDesktopBooster.Knowledge;

/// <summary>
/// El pedido listo para pegarle a un modelo de IA y que devuelva un archivo IMPORTABLE (el formato de
/// <see cref="KnowledgeExchange"/>). El texto vive en <c>Knowledge/ImportPrompt.es.txt</c>, embebido
/// como Resource igual que los diccionarios de idioma; acá sólo se lee y se le completan tres huecos.
///
/// Por qué la app completa naturalezas e idiomas en vez de dejarlos para tipear: en el archivo viajan
/// POR NOMBRE y el import los busca tal cual (las tildes cuentan). Un modelo que escribe "Ingles" no
/// falla: crea un segundo idioma al lado de "Inglés". Dándole los nombres EXACTOS que ya existen,
/// ese error no tiene por dónde entrar.
///
/// El texto está SÓLO en español a propósito: es el idioma del CONTENIDO del usuario, no el de la
/// interfaz. Con la UI en inglés se copia este mismo texto.
///
/// Si cambia el formato en <see cref="KnowledgeExchange"/>, cambia el .txt: describe ese lector.
/// </summary>
public static class ImportPrompt
{
    // Los huecos que completa la app. Los «{{…}}» del texto NO se tocan: son los que completa el
    // usuario a mano después de pegar (tema, nivel, cantidad…).
    private const string LanguagesSlot = "[[IDIOMAS]]";
    private const string NaturesSlot = "[[NATURALEZAS]]";
    private const string LibrarySlot = "[[BIBLIOTECA]]";

    // Sin idiomas / naturalezas cargados, el hueco pasa a ser uno más de los que completa el usuario,
    // con la misma forma «{{NOMBRE, por ejemplo …}}» que los demás: se lee como algo que falta.
    private const string LanguagesFallback = "{{IDIOMAS, por ejemplo \"Inglés, Francés\"}}";
    private const string NaturesFallback = "{{NATURALEZAS, por ejemplo \"Técnica, Legal, General\"}}";

    // El nombre de la biblioteca raíz SIEMPRE queda para completar, aunque haya una elegida en la
    // pestaña: lo habitual es pedir un tema NUEVO, y un nombre ya puesto que coincide con una
    // biblioteca existente termina, al importar, combinado adentro de ella sin que nadie lo decida.
    private const string LibraryFallback = "{{NOMBRE_BIBLIOTECA}}";

    /// <summary>
    /// El pedido completo, o null si el texto embebido no se pudo leer (lo informa la UI). Nunca tira.
    /// No toca la base: recibe los nombres ya leídos.
    /// </summary>
    public static string? Build(IEnumerable<Nature> natures, IEnumerable<Language> languages) =>
        LoadTemplate() is { } template
            ? Fill(template, natures.Select(n => n.Name), languages.Select(l => l.Name))
            : null;

    /// <summary>Completa los huecos de <paramref name="template"/>. Puro: sin recursos, sin base, sin UI.</summary>
    public static string Fill(string template, IEnumerable<string> natureNames, IEnumerable<string> languageNames) =>
        // Un solo final de línea, venga como venga el archivo del checkout.
        template.Replace("\r\n", "\n")
            .Replace(LanguagesSlot, JoinNames(languageNames) ?? LanguagesFallback)
            .Replace(NaturesSlot, JoinNames(natureNames) ?? NaturesFallback)
            .Replace(LibrarySlot, LibraryFallback);

    /// <summary>
    /// Los nombres separados por coma, TAL CUAL están guardados (el modelo tiene que devolverlos
    /// idénticos). Un nombre que trae una coma o una comilla doble va entre «»: suelto, la coma lo
    /// partiría en dos nombres y la comilla se confundiría con las del propio texto. Null = no hay.
    /// </summary>
    private static string? JoinNames(IEnumerable<string> names)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Contains(',') || n.Contains('"') ? "«" + n + "»" : n)
            .ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    private static string? LoadTemplate()
    {
        try
        {
            // Con el nombre del assembly en la URI (y no la forma corta «/Knowledge/…» que usa Loc):
            // la corta se resuelve contra el exe que arrancó, y ésta encuentra el texto igual cuando
            // el assembly se carga desde otro proceso.
            string assembly = typeof(ImportPrompt).Assembly.GetName().Name ?? "AmpzDesktopBooster";
            var info = Application.GetResourceStream(
                new Uri($"/{assembly};component/Knowledge/ImportPrompt.es.txt", UriKind.Relative));
            if (info is null) return null;

            using var reader = new StreamReader(info.Stream, Encoding.UTF8);
            string text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            // Recurso ausente o ilegible: null, y la pestaña dice que el texto no se pudo cargar.
            return null;
        }
    }
}

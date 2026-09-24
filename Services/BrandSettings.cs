using System;
using System.IO;
using System.Text.Json;
using AmpzDesktopBooster.Persistence;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// La MARCA del usuario en la barra: un PNG propio + un rótulo opcional, a la izquierda del todo
/// (antes de la fecha). No es un widget más — por eso no vive en <see cref="WidgetSettings"/>:
/// aquéllos son toggles de un set CERRADO que la app conoce de antemano, y éste trae CONTENIDO que
/// sólo existe en la máquina del usuario (un archivo, un texto). Mezclarlos habría metido un string
/// de path adentro de un modelo que es puro bool.
///
/// ⚠ La imagen se COPIA a %APPDATA%, no se referencia donde la dejaste. Guardar el path original
/// convertía a la barra en rehén de una carpeta ajena: renombrás el repo, limpiás Descargas, o
/// llevás la config a otra máquina, y el logo desaparece SIN AVISO (una barra a la que le falta un
/// pedazo se lee como app rota, no como archivo movido). Copiándola, la app es autocontenida y el
/// borrado sigue siendo tuyo: vaciar DataDir se la lleva, como al resto de la config.
/// </summary>
public sealed class BrandSettings
{
    /// <summary>Nombre del archivo YA IMPORTADO dentro de DataDir (no un path del usuario).</summary>
    public string ImageFile { get; set; } = "";

    /// <summary>De dónde salió originalmente. Sólo para mostrarlo en la config — no se lee de ahí.</summary>
    public string SourceName { get; set; } = "";

    public string Label { get; set; } = "";

    /// <summary>true = el texto va ANTES de la imagen; false = después.</summary>
    public bool LabelBefore { get; set; }

    /// <summary>
    /// Apagado por default, obvio: la barra de alguien que todavía no cargó su marca no tiene por qué
    /// reservarle lugar a un hueco.
    /// </summary>
    public bool Enabled { get; set; }

    public string ImagePath => ImageFile == "" ? "" : Path.Combine(AppPaths.DataDir, ImageFile);

    /// <summary>Hay imagen Y sigue estando en disco (alguien pudo vaciar DataDir a mano).</summary>
    public bool HasImage
    {
        get
        {
            try { return ImageFile != "" && File.Exists(ImagePath); }
            catch { return false; }
        }
    }

    /// <summary>Hay ALGO que pintar: sin imagen, un rótulo suelto sigue siendo una marca válida.</summary>
    public bool HasContent => Enabled && (HasImage || Label.Trim() != "");

    /// <summary>
    /// Copia la imagen elegida a DataDir y la deja como la marca activa. Devuelve false y deja el
    /// estado SIN TOCAR si el archivo no se pudo leer o copiar — a medio importar (con el nombre
    /// nuevo apuntando a un archivo que no llegó) la barra quedaría con un logo fantasma.
    ///
    /// El nombre de destino ROTA (brand1/brand2/…) en vez de pisar siempre "brand.png". La razón es
    /// el caché de WPF: <c>BitmapImage</c> cachea por URI, así que reemplazar el archivo dejando el
    /// mismo nombre te seguía mostrando el logo VIEJO hasta reiniciar la app. Con nombre nuevo, el
    /// URI cambia y la imagen nueva entra al toque.
    /// </summary>
    public bool ImportImage(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return false;

            string ext = Path.GetExtension(sourcePath);
            if (ext == "") ext = ".png";

            string previous = ImageFile;
            string target = $"brand_{DateTime.Now:yyyyMMddHHmmss}{ext}";
            File.Copy(sourcePath, Path.Combine(AppPaths.DataDir, target), overwrite: true);

            ImageFile = target;
            SourceName = Path.GetFileName(sourcePath);

            // La anterior ya no la ve nadie: si quedara, cada cambio de logo dejaría basura en
            // DataDir para siempre. Que falle el borrado no invalida la importación.
            if (previous != "" && previous != target)
            {
                try { File.Delete(Path.Combine(AppPaths.DataDir, previous)); } catch { }
            }
            return true;
        }
        catch
        {
            return false; // sin permisos, archivo tomado, disco lleno: la marca queda como estaba
        }
    }

    /// <summary>Saca la imagen (el rótulo sobrevive: se puede querer sólo texto).</summary>
    public void ClearImage()
    {
        string previous = ImageFile;
        ImageFile = "";
        SourceName = "";
        if (previous == "") return;
        try { File.Delete(Path.Combine(AppPaths.DataDir, previous)); } catch { }
    }

    // ---- Persistencia (mismo patrón que el resto: nunca voltea la app) ----

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(AppPaths.DataDir, "brand.json");

    public static BrandSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<BrandSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // corrupto o ilegible → defaults (marca apagada), no crasheamos
        }
        return new BrandSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // sin disco seguimos en memoria: la marca se ve hasta cerrar la app
        }
    }
}

using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace AmpzDesktopBooster.Services.Attention;

/// <summary>
/// Reproduce los .wav de atención con CONTROL DE VOLUMEN REAL y AMPLIFICACIÓN, vía NAudio. Por qué
/// NAudio y no las opciones del framework:
///   · System.Media.SoundPlayer → suena fuerte (volumen nativo) pero NO controla volumen.
///   · System.Windows.Media.MediaPlayer → controla volumen pero reproduce FLOJO y su tope es el
///     volumen nativo del wav (no amplifica) → los wavs de notificación, ya suaves, sonaban bajísimo.
///   · NAudio → AudioFileReader.Volume es una GANANCIA (multiplicador): >1 amplifica de verdad, así
///     que podés hacerlo sonar MÁS fuerte que el archivo original.
///
/// Lo comparten el AttentionService (sonido real) y la pestaña de config (botón "Probar").
/// </summary>
public static class AttentionSound
{
    /// <summary>
    /// Ganancia a volumen 100. El slider 0..100 mapea linealmente a 0..MaxGain, así que el 100% NO es
    /// el volumen nativo (que sonaba bajo) sino una amplificación clara. Si tu wav ya es fuerte y a
    /// tope distorsiona (clipping), bajás el slider. Los wavs de notificación tienen headroom de sobra.
    /// </summary>
    private const float MaxGain = 2.5f;

    // Reproducciones vivas: sin mantener la referencia, el GC se las lleva y corta el audio a la mitad.
    private static readonly List<IWavePlayer> Live = new();

    /// <summary>
    /// Reproduce un .wav al <paramref name="volume"/> dado (0..100). <paramref name="sound"/> puede ser
    /// el NOMBRE de un wav del sistema (se busca en %SystemRoot%\Media) o un PATH completo a un wav
    /// PROPIO del usuario. No-op si es "(Ninguno)", vacío, o el archivo no existe. Nunca tira.
    /// </summary>
    public static void Play(string sound, int volume)
    {
        if (string.IsNullOrEmpty(sound) || sound == AttentionSettings.NoneSound) return;

        try
        {
            // Path completo (wav propio) → tal cual; si no, nombre de un wav del sistema (carpeta Media).
            string path = File.Exists(sound)
                ? sound
                : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), "Media", sound);
            if (!File.Exists(path)) return;

            float gain = System.Math.Clamp(volume, 0, 100) / 100f * MaxGain;

            var reader = new AudioFileReader(path) { Volume = gain };
            var output = new WaveOutEvent();
            Live.Add(output);

            output.PlaybackStopped += (_, _) =>
            {
                try { output.Dispose(); } catch { }
                try { reader.Dispose(); } catch { }
                Live.Remove(output);
            };

            output.Init(reader);
            output.Play();
        }
        catch { /* sin sonido no pasa nada grave */ }
    }
}

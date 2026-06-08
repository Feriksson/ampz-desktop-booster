using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// Servicio central de notificaciones toast. Reemplaza los toasts del legacy (con su fade-out).
/// Acentos por tipo (verde anclado / azul movido / ámbar protección / rojo error) y apilado:
/// si saltan varios seguidos, se acomodan uno debajo del otro arriba-centro de la pantalla.
///
/// Thread-safe-ish: siempre marshalea al Dispatcher de UI (el enforcement corre en timers de UI,
/// pero por si acaso). Llamá a estos métodos desde cualquier lado sin preocuparte por el hilo.
/// </summary>
public static class Toasts
{
    public enum Kind { Pin, Move, Protect, Info, Error }

    private const double TopStart = 20;
    private const double Gap = 8;

    // Toasts vivos, para apilarlos. Se limpian al cerrarse.
    private static readonly List<ToastWindow> Live = new();

    public static void Pinned(string proc, string deskName) =>
        Show(Kind.Pin, $"📌  Anclado a  {deskName}", proc);

    public static void Unpinned(string proc, string deskName) =>
        Show(Kind.Info, $"📍  Desanclado de  {deskName}", proc);

    public static void MovedByPin(string what, string deskName) =>
        Show(Kind.Move, $"Movida por anclaje  →  {deskName}", what);

    public static void MovedByRestriction(string what, string fromDesk, string toDesk) =>
        Show(Kind.Move, $"{fromDesk}  →  {toDesk}  (no permitida)", what);

    public static void SendBlockedByRestriction(string proc, string deskName) =>
        Show(Kind.Protect, $"🔒  {deskName} está protegido", $"{proc} no está permitida ahí");

    public static void ProtectionOn(string deskName) =>
        Show(Kind.Protect, "🔒  Escritorio protegido", deskName);

    public static void ProtectionOff(string deskName) =>
        Show(Kind.Info, "🔓  Protección desactivada", deskName);

    public static void Whitelisted(string proc, string deskName) =>
        Show(Kind.Protect, $"Permitida en  {deskName}", proc);

    public static void Info(string title, string detail = "", string extra = "") => Show(Kind.Info, title, detail, extra);
    public static void Error(string title, string detail = "") => Show(Kind.Error, title, detail);

    private static void Show(Kind kind, string title, string detail, string extra = "")
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() => ShowOnUi(kind, title, detail, extra));
    }

    private static void ShowOnUi(Kind kind, string title, string detail, string extra = "")
    {
        var toast = new ToastWindow(title, detail, AccentFor(kind), extra);
        toast.Closed += (_, _) => { Live.Remove(toast); Restack(); };
        Live.Add(toast);

        double centerX = SystemParameters.WorkArea.Left + SystemParameters.WorkArea.Width / 2;
        toast.ShowAt(centerX, NextTop(toast));
    }

    /// <summary>Y donde va el toast nuevo: debajo de los que ya están vivos.</summary>
    private static double NextTop(ToastWindow incoming)
    {
        double y = TopStart;
        foreach (var t in Live)
        {
            if (ReferenceEquals(t, incoming)) continue;
            y += t.MeasuredHeight + Gap;
        }
        return y;
    }

    /// <summary>Reacomoda los toasts vivos cuando uno se cierra (para que no queden huecos).</summary>
    private static void Restack()
    {
        double y = TopStart;
        foreach (var t in Live)
        {
            t.Top = y;
            y += t.MeasuredHeight + Gap;
        }
    }

    private static Color AccentFor(Kind kind) => kind switch
    {
        Kind.Pin     => Color.FromRgb(0x44, 0xDD, 0x88), // verde
        Kind.Move    => Color.FromRgb(0x66, 0xAA, 0xFF), // azul
        Kind.Protect => Color.FromRgb(0xFF, 0xB7, 0x4D), // ámbar
        Kind.Error   => Color.FromRgb(0xE5, 0x73, 0x57), // rojo
        _            => Color.FromRgb(0x9A, 0x9A, 0x9A), // gris (info)
    };
}

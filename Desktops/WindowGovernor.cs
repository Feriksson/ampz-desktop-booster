using System;
using System.Windows;
using System.Windows.Threading;
using AmpzDesktopBooster.Interop;
using AmpzDesktopBooster.Services;

namespace AmpzDesktopBooster.Desktops;

/// <summary>
/// Motor de enforcement de pins + restricciones. Escucha "apareció una ventana" (WinEventHook)
/// y "se entró a un desktop" (lo llama App al cambiar de desk), y aplica:
///   - Pin: si el proceso está anclado y la ventana no está en su desk → la mueve y maximiza.
///   - Restricción: si la ventana aparece en un desk restringido y no está permitida → a MAIN.
///   - Al entrar a un desk restringido: escanea y manda a MAIN lo no permitido (cubre apps que
///     no disparan SHOW de forma confiable, como ciertas Electron — workaround del legacy).
///
/// Todo el trabajo se difiere al Dispatcher: el callback del hook NO se puede bloquear.
/// </summary>
public sealed class WindowGovernor : IDisposable
{
    private readonly DesktopService _desktops;
    private readonly PinStore _pins;
    private readonly RestrictionStore _restrictions;
    private readonly WinEventHook _hook = new();

    public WindowGovernor(DesktopService desktops, PinStore pins, RestrictionStore restrictions)
    {
        _desktops = desktops;
        _pins = pins;
        _restrictions = restrictions;
        _hook.WindowShown += OnWindowShown;
    }

    public void Start() => _hook.Install();
    public void Dispose() => _hook.Dispose();

    /// <summary>App lo llama al cambiar de desktop: aplica las restricciones del desk entrante.</summary>
    public void OnDesktopEntered(int idx) => Defer(() => EnforceRestrictionsOnDesk(idx), 300);

    private void OnWindowShown(IntPtr hwnd) => Defer(() => HandleWindow(hwnd), 50);

    private void HandleWindow(IntPtr hwnd)
    {
        if (!WindowMethods.IsRealTopLevel(hwnd)) return;
        string proc = WindowMethods.ProcessNameOf(hwnd);
        if (proc == "") return;

        // 1) Pin — si está anclado y no está en su desk, moverlo + maximizar. El pin guarda el NOMBRE
        //    del desk; resolvemos su índice ACÁ (FindExact). Si el desk fue borrado/renombrado y ya no
        //    existe (-1), no hacemos nada: degradar en silencio es mejor que mover a un índice errado.
        if (_pins.TryGet(proc, out string pinnedDeskName))
        {
            int pinnedDesk = _desktops.FindExact(pinnedDeskName);
            if (pinnedDesk >= 0 && VirtualDesktopAccessor.IsWindowOnDesktopNumber(hwnd, pinnedDesk) == 0)
            {
                VirtualDesktopAccessor.MoveWindowToDesktopNumber(hwnd, pinnedDesk);
                WindowMethods.Maximize(hwnd);
                Toasts.MovedByPin(TitleOrProc(hwnd, proc), pinnedDeskName);
                return; // ya se reubicó; su restricción se evalúa en el nuevo desk
            }
        }

        // 2) Restricción — si el desk donde apareció está restringido y el proc no está permitido, va
        //    al desk REFUGIO (el de rol Main, se llame como se llame — antes era el literal "MAIN").
        //    Resolvemos el ÍNDICE actual de la ventana a NOMBRE para consultar el store (clave por nombre).
        int deskOfWindow = VirtualDesktopAccessor.GetWindowDesktopNumber(hwnd);
        string deskOfWindowName = _desktops.GetName(deskOfWindow);
        if (_restrictions.IsRestricted(deskOfWindowName)
            && !_restrictions.IsExempt(proc)
            && !_restrictions.IsWhitelisted(deskOfWindowName, proc))
        {
            int main = _desktops.FindByNameFragment(DeskCatalog.FallbackDeskName);
            if (main >= 0 && main != deskOfWindow)
            {
                VirtualDesktopAccessor.MoveWindowToDesktopNumber(hwnd, main);
                Toasts.MovedByRestriction(TitleOrProc(hwnd, proc), deskOfWindowName, _desktops.GetName(main));
            }
        }
    }

    /// <summary>Título de la ventana recortado, o el nombre del proceso si no tiene título útil.</summary>
    private static string TitleOrProc(IntPtr hwnd, string proc)
    {
        string t = WindowMethods.TextOf(hwnd);
        if (t == "" || t == "Program Manager") return proc;
        return t.Length > 40 ? t[..37] + "..." : t;
    }

    private void EnforceRestrictionsOnDesk(int idx)
    {
        // El store está indexado por nombre: traducimos el índice entrante a su nombre actual.
        string deskName = _desktops.GetName(idx);
        if (!_restrictions.IsRestricted(deskName)) return;
        int main = _desktops.FindByNameFragment(DeskCatalog.FallbackDeskName);
        if (main < 0 || main == idx) return;

        WindowMethods.EnumWindows((hwnd, _) =>
        {
            if (!WindowMethods.IsRealTopLevel(hwnd)) return true;
            if (VirtualDesktopAccessor.IsWindowOnDesktopNumber(hwnd, idx) == 0) return true;
            // Fantasma del shell en ESTE desk (TextInputHost / "Experiencia de entrada de Windows",
            // UWP suspendidas): figura visible pero DWM la tiene cloaked → no es una ventana real, no
            // la tocamos. Acá el check es seguro porque ya confirmamos que está en el desk ACTUAL, así
            // que cloaked NO puede significar "está en otro escritorio virtual" (la trampa que evita
            // usar cloaking en IsRealTopLevel global).
            if (WindowMethods.IsCloaked(hwnd)) return true;
            string proc = WindowMethods.ProcessNameOf(hwnd);
            if (proc == "" || _restrictions.IsExempt(proc) || _restrictions.IsWhitelisted(deskName, proc))
                return true;
            VirtualDesktopAccessor.MoveWindowToDesktopNumber(hwnd, main);
            Toasts.MovedByRestriction(TitleOrProc(hwnd, proc), deskName, _desktops.GetName(main));
            return true;
        }, IntPtr.Zero);
    }

    private static void Defer(Action action, int delayMs)
    {
        // Un DispatcherTimer one-shot: corre en el thread UI tras el delay, sin bloquear el hook.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        t.Tick += (_, _) => { t.Stop(); try { action(); } catch { } };
        t.Start();
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Services;

/// <summary>
/// Registra la ventana WPF como una AppBar REAL de Windows.
/// La diferencia con un "always on top": acá Windows EMPUJA las demás ventanas.
/// Maximizar un programa ya no tapa la barra. Esa es la magia de SHAppBarMessage.
/// </summary>
public sealed class AppBarManager
{
    private readonly Window _window;
    private readonly int _edge;
    private readonly double _barHeightDip;

    private IntPtr _hWnd;
    private uint _callbackId;
    private bool _registered;

    // Rectángulo reservado (px físicos). Lo usamos para CLAVAR la barra: cualquier intento de
    // moverla (Aero Snap con Win+flechas, drag, lo que sea) se revierte a este rect.
    private RECT _lockedRect;
    private bool _hasLockedRect;

    public AppBarManager(Window window, double barHeightDip = 32, int edge = NativeMethods.ABE_BOTTOM)
    {
        _window = window;
        _barHeightDip = barHeightDip;
        _edge = edge;
    }

    /// <summary>Llamar UNA vez, después de que la ventana tenga handle (en SourceInitialized).</summary>
    public void Register()
    {
        _hWnd = new WindowInteropHelper(_window).Handle;

        // Mensaje de callback único: Windows nos avisa por acá cuando cambia la posición.
        _callbackId = (uint)NativeMethods.RegisterWindowMessage("AmpzBooster_AppBar_Callback_" + _hWnd);

        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hWnd,
            uCallbackMessage = _callbackId,
        };

        NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data);
        _registered = true;

        // Enganchamos el WndProc para escuchar ABN_POSCHANGED (taskbar se mueve, resolución cambia, etc.)
        HwndSource.FromHwnd(_hWnd)?.AddHook(WndProc);

        PositionBar();
    }

    /// <summary>Algoritmo OFICIAL de Microsoft. El orden importa: QUERYPOS y DESPUÉS SETPOS.</summary>
    public void PositionBar()
    {
        if (!_registered) return;

        double dpiScale = GetDpiScale();
        int barHeightPx = (int)Math.Round(_barHeightDip * dpiScale);

        int cx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int cy = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hWnd,
            uEdge = _edge,
        };

        // 1) Proponemos el rectángulo a pantalla completa en el borde elegido.
        data.rc = _edge switch
        {
            NativeMethods.ABE_TOP    => new RECT { left = 0, top = 0, right = cx, bottom = barHeightPx },
            NativeMethods.ABE_BOTTOM => new RECT { left = 0, top = cy - barHeightPx, right = cx, bottom = cy },
            NativeMethods.ABE_LEFT   => new RECT { left = 0, top = 0, right = barHeightPx, bottom = cy },
            NativeMethods.ABE_RIGHT  => new RECT { left = cx - barHeightPx, top = 0, right = cx, bottom = cy },
            _ => new RECT { left = 0, top = cy - barHeightPx, right = cx, bottom = cy },
        };

        // 2) QUERYPOS: Windows AJUSTA el rect para no pisar otras appbars (¡la taskbar es una appbar!).
        //    Por eso al pedir ABE_BOTTOM, Windows nos sube por ENCIMA de la taskbar. Eso es lo que querés.
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref data);

        // 3) Recalculamos el grosor sobre el rect ya ajustado.
        switch (_edge)
        {
            case NativeMethods.ABE_TOP:    data.rc.bottom = data.rc.top + barHeightPx; break;
            case NativeMethods.ABE_BOTTOM: data.rc.top = data.rc.bottom - barHeightPx; break;
            case NativeMethods.ABE_LEFT:   data.rc.right = data.rc.left + barHeightPx; break;
            case NativeMethods.ABE_RIGHT:  data.rc.left = data.rc.right - barHeightPx; break;
        }

        // 4) SETPOS: confirmamos. Windows reserva el espacio de verdad.
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data);

        // 5) Movemos la ventana WPF. OJO: el rect viene en píxeles físicos, WPF trabaja en DIPs → dividimos.
        _window.Left = data.rc.left / dpiScale;
        _window.Top = data.rc.top / dpiScale;
        _window.Width = (data.rc.right - data.rc.left) / dpiScale;
        _window.Height = (data.rc.bottom - data.rc.top) / dpiScale;

        // Guardamos el rect reservado: WndProc lo usa para revertir cualquier movimiento ajeno.
        _lockedRect = data.rc;
        _hasLockedRect = true;
    }

    /// <summary>Quitar la AppBar al cerrar. Si no lo hacés, Windows queda con el espacio reservado fantasma.</summary>
    public void Unregister()
    {
        if (!_registered) return;

        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hWnd,
        };
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_WINDOWPOSCHANGING: Windows avisa ANTES de mover/redimensionar. Forzamos x/y/cx/cy
        // de vuelta al rect reservado → la barra queda CLAVADA. Bloquea Aero Snap (Win+flechas),
        // drags y cualquier reubicación externa, sin impedir nuestro propio PositionBar (que ya
        // escribe exactamente este mismo rect, así que no hay conflicto).
        if (msg == WM_WINDOWPOSCHANGING && _hasLockedRect)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.x = _lockedRect.left;
            pos.y = _lockedRect.top;
            pos.cx = _lockedRect.right - _lockedRect.left;
            pos.cy = _lockedRect.bottom - _lockedRect.top;
            Marshal.StructureToPtr(pos, lParam, false);
            return IntPtr.Zero;
        }

        if (msg == _callbackId)
        {
            switch ((uint)wParam.ToInt32())
            {
                case NativeMethods.ABN_POSCHANGED:
                    PositionBar(); // la taskbar se movió / cambió la resolución → reacomodamos
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private const int WM_WINDOWPOSCHANGING = 0x0046;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    private double GetDpiScale()
    {
        var source = PresentationSource.FromVisual(_window);
        if (source?.CompositionTarget is not null)
            return source.CompositionTarget.TransformToDevice.M11;
        return 1.0;
    }
}

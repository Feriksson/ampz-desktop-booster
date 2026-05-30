using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using AmpzDesktopBooster.Interop;

namespace AmpzDesktopBooster.Services;

/// <summary>Snapshot inmutable de las métricas en un instante. La UI solo consume esto.</summary>
public readonly record struct SystemSnapshot(
    double CpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    double RamPercent,
    int BatteryPercent,
    bool OnAcPower,
    bool HasBattery,
    double NetDownKbps,
    double NetUpKbps);

/// <summary>
/// Junta CPU, RAM, batería y red. No sabe NADA de UI ni de WPF.
/// Esto es lo lindo de separar capas: lo podés testear o reusar en cualquier lado.
/// </summary>
public sealed class SystemMonitor
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasCpuBaseline;

    private long _prevBytesReceived, _prevBytesSent;
    private DateTime _prevNetTime = DateTime.UtcNow;
    private bool _hasNetBaseline;

    public SystemSnapshot Sample()
    {
        double cpu = SampleCpu();
        var (ramUsed, ramTotal, ramPct) = SampleRam();
        var (batt, ac, hasBatt) = SampleBattery();
        var (down, up) = SampleNetwork();

        return new SystemSnapshot(cpu, ramUsed, ramTotal, ramPct, batt, ac, hasBatt, down, up);
    }

    /// <summary>
    /// CPU% = 1 - (idle_delta / total_delta). Necesitamos DOS lecturas para tener un delta,
    /// por eso la primera muestra devuelve 0. Así funciona todo medidor de CPU, ojo.
    /// </summary>
    private double SampleCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        ulong idleT = idle.ToUInt64();
        ulong kernelT = kernel.ToUInt64(); // kernel INCLUYE idle en Windows
        ulong userT = user.ToUInt64();

        if (!_hasCpuBaseline)
        {
            _prevIdle = idleT; _prevKernel = kernelT; _prevUser = userT;
            _hasCpuBaseline = true;
            return 0;
        }

        ulong idleDelta = idleT - _prevIdle;
        ulong totalDelta = (kernelT - _prevKernel) + (userT - _prevUser);

        _prevIdle = idleT; _prevKernel = kernelT; _prevUser = userT;

        if (totalDelta == 0) return 0;
        double usage = (1.0 - (double)idleDelta / totalDelta) * 100.0;
        return Math.Clamp(usage, 0, 100);
    }

    private static (double usedGb, double totalGb, double percent) SampleRam()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref mem))
            return (0, 0, 0);

        const double gb = 1024d * 1024d * 1024d;
        double total = mem.ullTotalPhys / gb;
        double used = (mem.ullTotalPhys - mem.ullAvailPhys) / gb;
        return (used, total, mem.dwMemoryLoad);
    }

    private static (int percent, bool onAc, bool hasBattery) SampleBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
            return (0, true, false);

        bool hasBattery = status.BatteryFlag != 128 && status.BatteryLifePercent != 255;
        int percent = status.BatteryLifePercent == 255 ? 0 : status.BatteryLifePercent;
        bool onAc = status.ACLineStatus == 1;
        return (percent, onAc, hasBattery);
    }

    /// <summary>Velocidad de red = (bytes ahora - bytes antes) / segundos transcurridos.</summary>
    private (double downKbps, double upKbps) SampleNetwork()
    {
        long received = 0, sent = 0;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            // Algunas interfaces "Up" sin IPv4 tiran NetworkInformationException.
            // La saltamos en vez de dejar que voltee toda la app.
            try
            {
                var stats = ni.GetIPv4Statistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
            catch (NetworkInformationException)
            {
                // interfaz sin estadísticas IPv4 — la ignoramos
            }
        }

        var now = DateTime.UtcNow;
        double seconds = (now - _prevNetTime).TotalSeconds;

        if (!_hasNetBaseline || seconds <= 0)
        {
            _prevBytesReceived = received; _prevBytesSent = sent;
            _prevNetTime = now; _hasNetBaseline = true;
            return (0, 0);
        }

        double down = (received - _prevBytesReceived) / seconds / 1024.0; // KB/s
        double up = (sent - _prevBytesSent) / seconds / 1024.0;

        _prevBytesReceived = received; _prevBytesSent = sent;
        _prevNetTime = now;

        return (Math.Max(0, down), Math.Max(0, up));
    }
}

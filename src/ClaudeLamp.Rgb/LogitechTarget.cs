using System.Reflection;
using System.Runtime.InteropServices;
using ClaudeLamp.Core;

namespace ClaudeLamp.Rgb;

/// <summary>
/// Teclado, mouse e demais periféricos do Logitech G HUB, via LED SDK (LIGHTSYNC).
/// Salva a iluminação antes do sinal e a restaura no Release.
/// </summary>
public sealed class LogitechTarget : ILightTarget
{
    private const int DeviceTypeAll = 7; // MONOCHROME | RGB | PERKEY_RGB
    private bool _active;

    static LogitechTarget() => NativeSdk.EnsureResolver();

    public string Name => "Logitech G HUB";

    public static bool SdkAvailable => NativeSdk.Find(NativeSdk.LogitechDll) is not null;

    private void EnsureActive()
    {
        if (_active) return;
        if (!SdkAvailable) throw new FileNotFoundException($"{NativeSdk.LogitechDll} não encontrado na pasta x64 do app.");
        if (!Native.LogiLedInitWithName("ClaudeLamp"))
            throw new InvalidOperationException("O G HUB não aceitou a conexão. Ele está aberto e permite que aplicativos controlem a iluminação?");
        Native.LogiLedSetTargetDevice(DeviceTypeAll);
        Native.LogiLedSaveCurrentLighting();
        _active = true;
    }

    public string Detect()
    {
        EnsureActive();
        Native.LogiLedGetSdkVersion(out var ma, out var mi, out var bu);
        Release();
        return $"G HUB conectado (LED SDK {ma}.{mi}.{bu}) — vale para todos os periféricos Logitech com RGB";
    }

    public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default)
    {
        EnsureActive();
        var c = color.Scale(brightness);
        static int Pct(byte v) => (int)Math.Round(v * 100 / 255.0);
        Native.LogiLedSetLighting(Pct(c.R), Pct(c.G), Pct(c.B));
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CancellationToken ct = default)
    {
        Release();
        return Task.CompletedTask;
    }

    private void Release()
    {
        if (!_active) return;
        _active = false;
        Native.LogiLedRestoreLighting();
        Native.LogiLedShutdown();
    }

    public void Dispose() => Release();

    private static class Native
    {
        private const string Dll = "LogitechLedEnginesWrapper";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool LogiLedInitWithName([MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool LogiLedGetSdkVersion(out int major, out int minor, out int build);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool LogiLedSetTargetDevice(int targetDevice);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool LogiLedSaveCurrentLighting();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool LogiLedSetLighting(int redPercentage, int greenPercentage, int bluePercentage);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool LogiLedRestoreLighting();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void LogiLedShutdown();
    }
}

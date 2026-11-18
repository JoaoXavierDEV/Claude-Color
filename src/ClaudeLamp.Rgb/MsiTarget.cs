using System.Runtime.InteropServices;
using ClaudeLamp.Core;

namespace ClaudeLamp.Rgb;

/// <summary>
/// Placa de vídeo / placa-mãe MSI via Mystic Light SDK. Exige o processo rodando como administrador.
/// Salva estilo, cor e brilho de cada área antes do sinal e os restaura no Release.
/// </summary>
public sealed class MsiTarget : ILightTarget
{
    private sealed record Area(string Type, int Index, string Style, int R, int G, int B, int Bright, int MaxBright, string? StaticStyle, string[] Styles);

    /// <summary>Nomes de estilo de cor fixa: placas-mãe usam "Steady"; placas de vídeo costumam usar "NoAnimation".</summary>
    private static readonly string[] StaticStyles = ["Steady", "NoAnimation", "Static", "Always On", "AlwaysOn", "Solid"];

    private static bool _initialized;
    private static List<(string Type, int Index, string? StaticStyle, string[] Styles, int MaxBright)>? _info;
    private List<Area>? _saved;
    private bool _styled;

    static MsiTarget() => NativeSdk.EnsureResolver();

    public string Name => "MSI Mystic Light";

    /// <summary>Cada chamada ao Mystic Light leva centenas de ms: a GPU não pisca, só muda de cor.</summary>
    public bool Blinks => false;

    public static bool SdkAvailable => NativeSdk.Find(NativeSdk.MsiDll) is not null;

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        if (!SdkAvailable) throw new FileNotFoundException($"{NativeSdk.MsiDll} não encontrado.");
        int rc = Native.MLAPI_Initialize();
        if (rc != 0)
            throw new InvalidOperationException($"Mystic Light não inicializou ({Error(rc)}). O Claude Lamp está rodando como administrador?");
        _initialized = true;
    }

    private static string Error(int rc)
    {
        try { return Native.MLAPI_GetErrorMessage(rc, out var msg) == 0 ? $"{rc}: {msg}" : rc.ToString(); }
        catch { return rc.ToString(); }
    }

    private static IEnumerable<(string Type, int Index)> AllAreas()
    {
        int rc = Native.MLAPI_GetDeviceInfo(out var types, out var counts);
        if (rc != 0 || types is null || counts is null)
            throw new InvalidOperationException($"Mystic Light não listou dispositivos ({Error(rc)}).");
        for (int d = 0; d < types.Length; d++)
        {
            int n = int.TryParse(counts.ElementAtOrDefault(d), out var c) ? c : 0;
            for (int i = 0; i < n; i++) yield return (types[d], i);
        }
    }

    public IReadOnlyList<string> Detect()
    {
        EnsureInitialized();
        var lines = new List<string>();
        foreach (var (type, index) in AllAreas())
        {
            Native.MLAPI_GetLedInfo(type, index, out var name, out var styles);
            Native.MLAPI_GetLedStyle(type, index, out var style);
            lines.Add($"{type} #{index} {name} (estilo atual: {style}; {styles?.Length ?? 0} estilos)");
        }
        return lines.Count > 0 ? lines : ["nenhum dispositivo MSI encontrado"];
    }

    /// <summary>Informações que não mudam (estilos, brilho máximo): lidas uma vez, porque o SDK é lento.</summary>
    private static List<(string Type, int Index, string? StaticStyle, string[] Styles, int MaxBright)> Info()
    {
        if (_info is not null) return _info;
        var list = new List<(string, int, string?, string[], int)>();
        foreach (var (type, index) in AllAreas())
        {
            Native.MLAPI_GetLedInfo(type, index, out _, out var styles);
            Native.MLAPI_GetLedMaxBright(type, index, out var maxBright);
            styles ??= [];
            var found = StaticStyles.FirstOrDefault(st => styles.Contains(st, StringComparer.OrdinalIgnoreCase));
            var staticStyle = found is null ? null : styles.First(x => string.Equals(x, found, StringComparison.OrdinalIgnoreCase));
            list.Add((type, index, staticStyle, styles, maxBright));
        }
        Storage.Log("msi: áreas = " + (list.Count == 0 ? "(nenhuma)" : string.Join("; ", list.Select(a =>
            $"{a.Item1}#{a.Item2} fixo={a.Item3 ?? "?"} brilhoMax={a.Item5} estilos=[{string.Join(",", a.Item4)}]"))));
        return _info = list;
    }

    /// <summary>Inicializa o SDK e lê as informações fixas antes do primeiro sinal.</summary>
    private static readonly object SdkLock = new();

    public static void Warmup()
    {
        try { lock (SdkLock) { EnsureInitialized(); Info(); } }
        catch (Exception ex) { Storage.Log($"msi: aquecimento: {ex.Message}"); }
    }

    private void Save()
    {
        if (_saved is not null) return;
        var list = new List<Area>();
        foreach (var (type, index, staticStyle, styles, maxBright) in Info())
        {
            Native.MLAPI_GetLedStyle(type, index, out var style);
            Native.MLAPI_GetLedColor(type, index, out var r, out var g, out var b);
            Native.MLAPI_GetLedBright(type, index, out var bright);
            list.Add(new Area(type, index, style ?? "", r, g, b, bright, maxBright, staticStyle, styles));
        }
        _saved = list;
        _styled = false;
    }

    public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default)
    {
        lock (SdkLock) return ShowLocked(color, brightness);
    }

    private Task ShowLocked(RgbColor color, int brightness)
    {
        EnsureInitialized();
        Save();
        foreach (var a in _saved!)
        {
            int rcStyle = 0, rcBright = 0;
            // Estilo e brilho só na primeira cor do sinal; depois, apenas a cor (cada chamada ao SDK é lenta).
            if (!_styled)
            {
                if (a.StaticStyle is { } st && !string.Equals(a.Style, st, StringComparison.OrdinalIgnoreCase))
                    rcStyle = Native.MLAPI_SetLedStyle(a.Type, a.Index, st);
                if (a.MaxBright > 0 && a.Bright != a.MaxBright) rcBright = Native.MLAPI_SetLedBright(a.Type, a.Index, a.MaxBright);
            }
            var c = color.Scale(brightness);
            int rcColor = Native.MLAPI_SetLedColor(a.Type, a.Index, c.R, c.G, c.B);
            if (rcStyle != 0 || rcColor != 0 || rcBright != 0)
                Storage.Log($"msi: {a.Type}#{a.Index} estilo={Error(rcStyle)} cor={Error(rcColor)} brilho={Error(rcBright)}");
        }
        _styled = true;
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CancellationToken ct = default)
    {
        lock (SdkLock) Restore();
        return Task.CompletedTask;
    }

    private void Restore()
    {
        if (_saved is null) return;
        foreach (var a in _saved)
        {
            try
            {
                Native.MLAPI_SetLedColor(a.Type, a.Index, a.R, a.G, a.B);
                if (!string.IsNullOrEmpty(a.Style)) Native.MLAPI_SetLedStyle(a.Type, a.Index, a.Style);
                if (a.MaxBright > 0 && a.Bright != a.MaxBright) Native.MLAPI_SetLedBright(a.Type, a.Index, a.Bright);
            }
            catch (Exception ex) { Storage.Log($"msi: restaurar {a.Type}#{a.Index}: {ex.Message}"); }
        }
        _saved = null;
    }

    public void Dispose() => Restore();

    private static class Native
    {
        private const string Dll = "MysticLight_SDK_x64";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MLAPI_Initialize();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MLAPI_GetDeviceInfo(
            [Out, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] pDevType,
            [Out, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] pLedCount);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MLAPI_GetLedInfo([MarshalAs(UnmanagedType.BStr)] string type, int index,
            [Out, MarshalAs(UnmanagedType.BStr)] out string pName,
            [Out, MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] out string[] pLedStyles);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MLAPI_GetLedColor([MarshalAs(UnmanagedType.BStr)] string type, int index, out int r, out int g, out int b);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MLAPI_GetLedStyle([MarshalAs(UnmanagedType.BStr)] string type, int index,
            [Out, MarshalAs(UnmanagedType.BStr)] out string style);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MLAPI_GetLedMaxBright([MarshalAs(UnmanagedType.BStr)] string type, int index, out int maxLevel);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MLAPI_GetLedBright([MarshalAs(UnmanagedType.BStr)] string type, int index, out int level);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MLAPI_SetLedColor([MarshalAs(UnmanagedType.BStr)] string type, int index, int r, int g, int b);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MLAPI_SetLedStyle([MarshalAs(UnmanagedType.BStr)] string type, int index,
            [MarshalAs(UnmanagedType.BStr)] string style);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MLAPI_SetLedBright([MarshalAs(UnmanagedType.BStr)] string type, int index, int level);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MLAPI_GetErrorMessage(int errorCode, [Out, MarshalAs(UnmanagedType.BStr)] out string pDesc);
    }
}

using System.Reflection;
using System.Runtime.InteropServices;

namespace ClaudeLamp.Rgb;

/// <summary>Localiza as DLLs nativas dos SDKs (não versionadas; ficam em &lt;app&gt;\x64).</summary>
public static class NativeSdk
{
    public const string CorsairDll = "iCUESDK.x64_2019.dll";
    public const string LogitechDll = "LogitechLedEnginesWrapper.dll";
    public const string MsiDll = "MysticLight_SDK_x64.dll";

    public static IEnumerable<string> Folders =>
    [
        Path.Combine(AppContext.BaseDirectory, "x64"),
        AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-lamp", "bin", "x64"),
        // Cópia do SDK da MSI que o iCUE instala com o plugin MSI.
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Corsair", "Corsair iCUE5 Software", "plugins", "MSI"),
    ];

    public static string? Find(string dll) =>
        Folders.Select(f => Path.Combine(f, dll)).FirstOrDefault(File.Exists);

    private static int _resolverSet;

    /// <summary>Um único resolver por assembly: mapeia os nomes do [DllImport] para os caminhos encontrados.</summary>
    public static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeSdk).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        var dll = name switch
        {
            "LogitechLedEnginesWrapper" => LogitechDll,
            "MysticLight_SDK_x64" => MsiDll,
            _ => null,
        };
        return dll is not null && Find(dll) is { } full ? NativeLibrary.Load(full) : IntPtr.Zero;
    }
}

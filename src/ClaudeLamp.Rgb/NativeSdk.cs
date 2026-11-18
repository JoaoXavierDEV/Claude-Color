namespace ClaudeLamp.Rgb;

/// <summary>Localiza as DLLs nativas dos SDKs (não versionadas; ficam em &lt;app&gt;\x64).</summary>
public static class NativeSdk
{
    public const string CorsairDll = "iCUESDK.x64_2019.dll";
    public const string LogitechDll = "LogitechLedEnginesWrapper.dll";

    public static IEnumerable<string> Folders =>
    [
        Path.Combine(AppContext.BaseDirectory, "x64"),
        AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-lamp", "bin", "x64"),
    ];

    public static string? Find(string dll) =>
        Folders.Select(f => Path.Combine(f, dll)).FirstOrDefault(File.Exists);
}

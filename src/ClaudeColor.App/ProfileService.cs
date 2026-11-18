using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Troca de perfil vinda da janela ou da bandeja.</summary>
internal static class ProfileService
{
    /// <summary>Disparado (na thread de quem trocou) depois que o perfil ativo mudou.</summary>
    public static event Action? Changed;

    /// <summary>Devolve os dispositivos do perfil anterior aos seus softwares e ativa <paramref name="name"/>.</summary>
    public static async Task ActivateAsync(string name)
    {
        if (SignalHost.Current is { } host) await host.PcRgb.ReleaseAllAsync();
        var cfg = Storage.LoadConfig();
        cfg.ActivateProfile(name);
        Storage.SaveConfig(cfg);
        Storage.Log($"perfil: {cfg.ActiveProfile} ({cfg.Active().Summary()})");
        Changed?.Invoke();
    }

    public static void RaiseChanged() => Changed?.Invoke();
}

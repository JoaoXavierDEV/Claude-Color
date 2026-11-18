using System.Text.Json.Serialization;

namespace ClaudeColor.Core;

/// <summary>
/// Uma combinação de controladores: quais integrações acendem e quais dispositivos do OpenRGB entram.
/// O perfil ativo é copiado para <see cref="AppConfig.PcRgb"/> e <see cref="LampConfig.Enabled"/>, que
/// continuam sendo o estado que os sinais usam.
/// </summary>
public sealed class DeviceProfile
{
    public string Name { get; set; } = "";
    public bool OpenRgb { get; set; } = true;
    /// <summary>"Fabricante Nome" dos dispositivos do OpenRGB. Null = todos que nenhuma integração nativa cobre.</summary>
    public List<string>? OpenRgbDevices { get; set; }
    public bool Corsair { get; set; }
    public bool Logitech { get; set; }
    public bool Msi { get; set; }
    public bool Lamp { get; set; }

    /// <summary>O perfil "Padrão": só o OpenRGB, com todos os dispositivos.</summary>
    public static DeviceProfile Default() => new() { Name = Profiles.DefaultName };

    public static DeviceProfile CaptureFrom(AppConfig cfg, string name) => new()
    {
        Name = name,
        OpenRgb = cfg.PcRgb.OpenRgb.Enabled,
        OpenRgbDevices = cfg.PcRgb.OpenRgb.Devices is { } d ? [.. d] : null,
        Corsair = cfg.PcRgb.Corsair,
        Logitech = cfg.PcRgb.Logitech,
        Msi = cfg.PcRgb.Msi,
        Lamp = cfg.Lamp.Enabled,
    };

    public void ApplyTo(AppConfig cfg)
    {
        cfg.PcRgb.OpenRgb.Enabled = OpenRgb;
        cfg.PcRgb.OpenRgb.Devices = OpenRgbDevices is { } d ? [.. d] : null;
        cfg.PcRgb.Corsair = Corsair;
        cfg.PcRgb.Logitech = Logitech;
        cfg.PcRgb.Msi = Msi;
        cfg.Lamp.Enabled = Lamp;
    }

    public DeviceProfile Copy(string name)
    {
        var p = (DeviceProfile)MemberwiseClone();
        p.Name = name;
        p.OpenRgbDevices = OpenRgbDevices is { } d ? [.. d] : null;
        return p;
    }

    /// <summary>Igual ao Padrão (ignorando o nome)?</summary>
    [JsonIgnore]
    public bool IsDefaultSetup =>
        OpenRgb && OpenRgbDevices is null && !Corsair && !Logitech && !Msi && !Lamp;

    /// <summary>Resumo legível: "OpenRGB (2 dispositivos), Corsair iCUE".</summary>
    public string Summary()
    {
        var list = new List<string>();
        if (OpenRgb)
            list.Add(OpenRgbDevices is { } d ? $"OpenRGB ({d.Count} dispositivo{(d.Count == 1 ? "" : "s")})" : "OpenRGB (todos)");
        if (Corsair) list.Add(DeviceRouting.Corsair);
        if (Logitech) list.Add(DeviceRouting.Logitech);
        if (Msi) list.Add(DeviceRouting.Msi);
        if (Lamp) list.Add("lâmpada Wi-Fi");
        return list.Count == 0 ? "nada" : string.Join(", ", list);
    }
}

/// <summary>Operações sobre os perfis de <see cref="AppConfig"/>.</summary>
public static class Profiles
{
    public const string DefaultName = "Padrão";
    /// <summary>Nome do perfil criado a partir de uma config sem perfis (2.0) que já não era o padrão.</summary>
    public const string ImportedName = "Meu perfil";

    public static bool IsDefault(string name) => string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase);

    public static DeviceProfile? Find(this AppConfig cfg, string name) =>
        cfg.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static DeviceProfile Active(this AppConfig cfg) => cfg.Find(cfg.ActiveProfile) ?? cfg.Find(DefaultName)!;

    public static bool ActiveIsDefault(this AppConfig cfg) => IsDefault(cfg.Active().Name);

    /// <summary>
    /// Garante o Padrão (sempre só OpenRGB) e um perfil ativo válido. Numa config sem perfis, a configuração
    /// atual vira "Meu perfil" se não for igual ao Padrão. Retorna true se mudou algo.
    /// </summary>
    public static bool EnsureProfiles(this AppConfig cfg)
    {
        bool changed = false;
        if (cfg.Profiles.Count == 0)
        {
            cfg.Profiles.Add(DeviceProfile.Default());
            var current = DeviceProfile.CaptureFrom(cfg, ImportedName);
            if (current.IsDefaultSetup) cfg.ActiveProfile = DefaultName;
            else
            {
                cfg.Profiles.Add(current);
                cfg.ActiveProfile = ImportedName;
            }
            return true;
        }

        // Nomes vazios ou repetidos (JSON editado à mão).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int before = cfg.Profiles.Count;
        cfg.Profiles = cfg.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.Name) && seen.Add(p.Name.Trim())).ToList();
        changed |= cfg.Profiles.Count != before;

        var def = cfg.Find(DefaultName);
        if (def is null)
        {
            cfg.Profiles.Insert(0, DeviceProfile.Default());
            changed = true;
        }
        else if (!def.IsDefaultSetup || def.Name != DefaultName)
        {
            cfg.Profiles[cfg.Profiles.IndexOf(def)] = DeviceProfile.Default();
            changed = true;
        }

        if (cfg.Find(cfg.ActiveProfile) is not { } active)
        {
            cfg.ActiveProfile = DefaultName;
            DeviceProfile.Default().ApplyTo(cfg);
            changed = true;
        }
        else if (cfg.ActiveProfile != active.Name)
        {
            cfg.ActiveProfile = active.Name;
            changed = true;
        }
        return changed;
    }

    /// <summary>Torna o perfil ativo e copia as escolhas dele para a config.</summary>
    public static void ActivateProfile(this AppConfig cfg, string name)
    {
        var p = cfg.Find(name) ?? throw new InvalidOperationException($"Perfil \"{name}\" não existe.");
        cfg.ActiveProfile = p.Name;
        p.ApplyTo(cfg);
    }

    /// <summary>Guarda as escolhas atuais no perfil ativo. O Padrão nunca muda.</summary>
    public static void SaveActiveProfile(this AppConfig cfg)
    {
        var active = cfg.Active();
        if (IsDefault(active.Name))
        {
            active.ApplyTo(cfg); // desfaz qualquer mudança feita com o Padrão ativo
            return;
        }
        cfg.Profiles[cfg.Profiles.IndexOf(active)] = DeviceProfile.CaptureFrom(cfg, active.Name);
    }

    public static DeviceProfile CreateProfile(this AppConfig cfg, string name, string copyFrom)
    {
        name = ValidName(cfg, name);
        var source = cfg.Find(copyFrom) ?? cfg.Active();
        var p = source.Copy(name);
        cfg.Profiles.Add(p);
        return p;
    }

    public static void RenameProfile(this AppConfig cfg, string from, string to)
    {
        if (IsDefault(from)) throw new InvalidOperationException("O perfil Padrão não pode ser renomeado.");
        var p = cfg.Find(from) ?? throw new InvalidOperationException($"Perfil \"{from}\" não existe.");
        if (string.Equals(from.Trim(), to.Trim(), StringComparison.Ordinal)) return;
        to = string.Equals(from, to.Trim(), StringComparison.OrdinalIgnoreCase) ? to.Trim() : ValidName(cfg, to);
        bool wasActive = string.Equals(cfg.ActiveProfile, p.Name, StringComparison.OrdinalIgnoreCase);
        p.Name = to;
        if (wasActive) cfg.ActiveProfile = to;
    }

    public static void DeleteProfile(this AppConfig cfg, string name)
    {
        if (IsDefault(name)) throw new InvalidOperationException("O perfil Padrão não pode ser excluído.");
        var p = cfg.Find(name) ?? throw new InvalidOperationException($"Perfil \"{name}\" não existe.");
        bool wasActive = string.Equals(cfg.ActiveProfile, p.Name, StringComparison.OrdinalIgnoreCase);
        cfg.Profiles.Remove(p);
        if (wasActive) cfg.ActivateProfile(DefaultName);
    }

    /// <summary>Um nome livre a partir de <paramref name="baseName"/>: "Perfil 2", "Perfil 3"...</summary>
    public static string FreeName(this AppConfig cfg, string baseName)
    {
        if (cfg.Find(baseName) is null) return baseName;
        for (int i = 2; ; i++)
            if (cfg.Find($"{baseName} {i}") is null) return $"{baseName} {i}";
    }

    private static string ValidName(AppConfig cfg, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Dê um nome ao perfil.");
        if (name.Length > 40) throw new InvalidOperationException("Use um nome com até 40 caracteres.");
        if (cfg.Find(name) is not null) throw new InvalidOperationException($"Já existe um perfil \"{name}\".");
        return name;
    }
}

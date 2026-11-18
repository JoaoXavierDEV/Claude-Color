using System.Text.Json;

namespace ClaudeColor.Core.Tests;

[Collection("Storage")]
public class ConfigMigrationTests
{
    private const string Legacy1x = """
        {
          "deviceId": "abc123",
          "localKey": "0123456789abcdef",
          "ip": "192.168.1.10",
          "protocolVersion": "3.5",
          "dpsProfile": "v2",
          "signalsEnabled": true,
          "pcRgb": { "corsair": true, "logitech": true, "msi": true,
                     "openRgb": { "enabled": true, "devices": [ "CoolerMaster ARGB" ] } },
          "events": { "done": { "color": "#00FF40", "brightness": 100 } }
        }
        """;

    [Fact]
    public void Flat_1x_lamp_fields_move_to_lamp_section()
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(Legacy1x, Storage.Json)!;
        Assert.True(cfg.MigrateLegacy());

        Assert.True(cfg.Lamp.Enabled);
        Assert.True(cfg.Lamp.IsActive);
        Assert.Equal("abc123", cfg.Lamp.DeviceId);
        Assert.Equal("192.168.1.10", cfg.Lamp.Ip);
        Assert.Equal("3.5", cfg.Lamp.ProtocolVersion);
        Assert.Equal("v2", cfg.Lamp.DpsProfile);
        // As escolhas explícitas do usuário continuam valendo.
        Assert.True(cfg.PcRgb.Corsair);
        Assert.True(cfg.PcRgb.Msi);
        Assert.Equal(["CoolerMaster ARGB"], cfg.PcRgb.OpenRgb.Devices);

        // Gravado de novo, não sobra nenhum campo antigo na raiz.
        var json = JsonSerializer.Serialize(cfg, Storage.Json);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("deviceId", out _));
        Assert.Equal("abc123", doc.RootElement.GetProperty("lamp").GetProperty("deviceId").GetString());
        Assert.False(JsonSerializer.Deserialize<AppConfig>(json, Storage.Json)!.MigrateLegacy());
    }

    [Fact]
    public void Fresh_install_has_no_lamp_and_openrgb_on()
    {
        var cfg = new AppConfig();
        Assert.False(cfg.MigrateLegacy());
        Assert.False(cfg.Lamp.Enabled);
        Assert.False(cfg.Lamp.IsActive);
        Assert.True(cfg.PcRgb.OpenRgb.Enabled);
        Assert.True(cfg.LaunchFromHook);
    }

    [Fact]
    public void Data_folder_is_copied_from_1x_once()
    {
        var legacy = Directory.CreateTempSubdirectory("claude-lamp-1x-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(legacy, "config.json"), Legacy1x);
            File.WriteAllText(Path.Combine(legacy, "state.json"), """{"signalGeneration": 7}""");
            File.Delete(Storage.ConfigPath);
            File.Delete(Storage.StatePath);

            Assert.True(Storage.MigrateFrom(legacy));
            var cfg = Storage.LoadConfig();
            Assert.Equal("abc123", cfg.Lamp.DeviceId);
            Assert.Equal(7, Storage.LoadState().SignalGeneration);

            // Já existe config: não sobrescreve.
            Assert.False(Storage.MigrateFrom(legacy));
        }
        finally { Directory.Delete(legacy, true); }
    }

    [Fact]
    public void Pending_signal_only_when_recent_event()
    {
        var rt = new RuntimeState { LastSignal = ClaudeEvents.Question, LastSignalAt = DateTimeOffset.Now, SignalGeneration = 3 };
        Assert.Equal((ClaudeEvents.Question, 3L), rt.PendingSignal(TimeSpan.FromSeconds(20)));

        rt.LastSignalAt = DateTimeOffset.Now.AddMinutes(-5);
        Assert.Null(rt.PendingSignal(TimeSpan.FromSeconds(20)));

        rt.LastSignal = "manual";
        rt.LastSignalAt = DateTimeOffset.Now;
        Assert.Null(rt.PendingSignal(TimeSpan.FromSeconds(20)));
    }
}

public class DeviceRoutingTests
{
    private static PcRgbConfig Native(bool corsair = false, bool logitech = false, bool msi = false) =>
        new() { Corsair = corsair, Logitech = logitech, Msi = msi };

    [Fact]
    public void Without_native_integrations_openrgb_takes_everything()
    {
        var cfg = Native();
        Assert.True(DeviceRouting.DefaultSelected(DeviceRouting.TypeDram, "Corsair", "Vengeance RGB Pro", cfg));
        Assert.True(DeviceRouting.DefaultSelected(DeviceRouting.TypeGpu, "MSI", "RTX 4070", cfg));
        Assert.True(DeviceRouting.DefaultSelected(DeviceRouting.TypeKeyboard, "Logitech", "G915", cfg));
        Assert.True(DeviceRouting.DefaultSelected(DeviceRouting.TypeLedStrip, "CoolerMaster", "ARGB Gen 2", cfg));
    }

    [Fact]
    public void Active_native_integrations_keep_their_devices()
    {
        var cfg = Native(corsair: true, logitech: true, msi: true);
        Assert.Equal(DeviceRouting.Corsair, DeviceRouting.NativeOwner(DeviceRouting.TypeDram, "Corsair", "Vengeance", cfg));
        Assert.Equal(DeviceRouting.Logitech, DeviceRouting.NativeOwner(DeviceRouting.TypeMouse, "Logitech", "G502", cfg));
        Assert.Equal(DeviceRouting.Msi, DeviceRouting.NativeOwner(DeviceRouting.TypeGpu, "MSI", "RTX 4070", cfg));
        Assert.Equal(DeviceRouting.Msi, DeviceRouting.NativeOwner(DeviceRouting.TypeMotherboard, "", "MSI MAG B650", cfg));
        // MSI só cobre GPU e placa-mãe; fans continuam no OpenRGB.
        Assert.Null(DeviceRouting.NativeOwner(DeviceRouting.TypeLedStrip, "MSI", "MSI ARGB hub", cfg));
        Assert.True(DeviceRouting.DefaultSelected(DeviceRouting.TypeLedStrip, "CoolerMaster", "ARGB Gen 2", cfg));
    }

    [Fact]
    public void Type_names_cover_protocol()
    {
        Assert.Equal("Memória RAM", DeviceRouting.TypeName(DeviceRouting.TypeDram));
        Assert.Equal("Keypad", DeviceRouting.TypeName(DeviceRouting.TypeKeypad));
        Assert.Equal("tipo 99", DeviceRouting.TypeName(99));
    }
}

public class ProfileTests
{
    [Fact]
    public void Fresh_config_has_only_default_profile_with_openrgb()
    {
        var cfg = new AppConfig();
        Assert.True(cfg.EnsureProfiles());
        var p = Assert.Single(cfg.Profiles);
        Assert.Equal(Profiles.DefaultName, p.Name);
        Assert.Equal(Profiles.DefaultName, cfg.ActiveProfile);
        Assert.True(cfg.ActiveIsDefault());
        Assert.True(cfg.PcRgb.OpenRgb.Enabled);
        Assert.Null(cfg.PcRgb.OpenRgb.Devices);
        Assert.False(cfg.PcRgb.Corsair || cfg.PcRgb.Logitech || cfg.PcRgb.Msi || cfg.Lamp.Enabled);
        Assert.False(cfg.EnsureProfiles());
    }

    [Fact]
    public void Existing_custom_setup_is_imported_and_kept_active()
    {
        var cfg = new AppConfig();
        cfg.PcRgb.Corsair = true;
        cfg.PcRgb.Msi = true;
        cfg.PcRgb.OpenRgb.Devices = ["CoolerMaster ARGB"];
        cfg.Lamp.Enabled = true;

        Assert.True(cfg.EnsureProfiles());
        Assert.Equal(2, cfg.Profiles.Count);
        Assert.Equal(Profiles.ImportedName, cfg.ActiveProfile);
        Assert.True(cfg.PcRgb.Corsair);
        Assert.Equal(["CoolerMaster ARGB"], cfg.PcRgb.OpenRgb.Devices);
        Assert.Equal("OpenRGB (1 dispositivo), Corsair iCUE, MSI Mystic Light, lâmpada Wi-Fi", cfg.Active().Summary());
    }

    [Fact]
    public void Activating_copies_choices_and_default_never_changes()
    {
        var cfg = new AppConfig();
        cfg.EnsureProfiles();
        var mine = cfg.CreateProfile("Meu PC", Profiles.DefaultName);
        mine.Corsair = true;
        mine.OpenRgbDevices = ["A"];

        cfg.ActivateProfile("meu pc");
        Assert.Equal("Meu PC", cfg.ActiveProfile);
        Assert.True(cfg.PcRgb.Corsair);
        Assert.Equal(["A"], cfg.PcRgb.OpenRgb.Devices);

        cfg.PcRgb.Logitech = true;
        cfg.SaveActiveProfile();
        Assert.True(cfg.Find("Meu PC")!.Logitech);

        cfg.ActivateProfile(Profiles.DefaultName);
        Assert.False(cfg.PcRgb.Corsair || cfg.PcRgb.Logitech);
        cfg.PcRgb.Msi = true;          // editado com o Padrão ativo...
        cfg.SaveActiveProfile();
        Assert.False(cfg.PcRgb.Msi);   // ...é desfeito
        Assert.True(cfg.Active().IsDefaultSetup);
    }

    [Fact]
    public void Default_cannot_be_renamed_or_deleted_and_deleting_active_falls_back()
    {
        var cfg = new AppConfig();
        cfg.EnsureProfiles();
        Assert.Throws<InvalidOperationException>(() => cfg.RenameProfile(Profiles.DefaultName, "X"));
        Assert.Throws<InvalidOperationException>(() => cfg.DeleteProfile(Profiles.DefaultName));
        Assert.Throws<InvalidOperationException>(() => cfg.CreateProfile("padrão", Profiles.DefaultName));

        var p = cfg.CreateProfile("A", Profiles.DefaultName);
        p.Msi = true;
        cfg.ActivateProfile("A");
        cfg.RenameProfile("A", "B");
        Assert.Equal("B", cfg.ActiveProfile);
        cfg.DeleteProfile("B");
        Assert.Equal(Profiles.DefaultName, cfg.ActiveProfile);
        Assert.False(cfg.PcRgb.Msi);
        Assert.Equal("Perfil 2", new AppConfig { Profiles = [new() { Name = "Perfil" }] }.FreeName("Perfil"));
    }

    [Fact]
    public void Hand_edited_json_is_repaired_and_round_trips()
    {
        var json = """
            { "activeProfile": "sumiu",
              "profiles": [ { "name": "Padrão", "corsair": true }, { "name": "X", "msi": true }, { "name": "x" }, { "name": "" } ] }
            """;
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Storage.Json)!;
        Assert.True(cfg.EnsureProfiles());
        Assert.Equal(["Padrão", "X"], cfg.Profiles.Select(p => p.Name));
        Assert.True(cfg.Find(Profiles.DefaultName)!.IsDefaultSetup);
        Assert.Equal(Profiles.DefaultName, cfg.ActiveProfile);

        var again = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg, Storage.Json), Storage.Json)!;
        Assert.False(again.EnsureProfiles());
        Assert.True(again.Find("X")!.Msi);
    }
}

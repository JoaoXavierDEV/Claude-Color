namespace ClaudeColor.Core;

/// <summary>
/// Decide quem controla cada dispositivo do OpenRGB: o próprio OpenRGB ou uma integração nativa ativa
/// (iCUE, G HUB, Mystic Light). Evita que dois softwares disputem o mesmo dispositivo.
/// </summary>
public static class DeviceRouting
{
    // Tipos de dispositivo do SDK do OpenRGB (device_type).
    public const int TypeMotherboard = 0, TypeDram = 1, TypeGpu = 2, TypeCooler = 3, TypeLedStrip = 4,
        TypeKeyboard = 5, TypeMouse = 6, TypeMousemat = 7, TypeHeadset = 8, TypeHeadsetStand = 9, TypeGamepad = 10,
        TypeLight = 11, TypeSpeaker = 12, TypeVirtual = 13, TypeStorage = 14, TypeCase = 15, TypeMicrophone = 16,
        TypeAccessory = 17, TypeKeypad = 18;

    public const string Corsair = "Corsair iCUE", Logitech = "Logitech G HUB", Msi = "MSI Mystic Light";

    /// <summary>A integração nativa ativa que já cobre este dispositivo, ou null se ele é do OpenRGB.</summary>
    public static string? NativeOwner(int type, string vendor, string name, PcRgbConfig cfg)
    {
        bool Is(string brand) =>
            vendor.Contains(brand, StringComparison.OrdinalIgnoreCase) || name.StartsWith(brand, StringComparison.OrdinalIgnoreCase);

        if (cfg.Corsair && Is("Corsair")) return Corsair;
        if (cfg.Logitech && Is("Logitech")) return Logitech;
        if (cfg.Msi && type is TypeGpu or TypeMotherboard && Is("MSI")) return Msi;
        return null;
    }

    /// <summary>Marcado por padrão (antes de você escolher na lista): tudo que nenhuma integração nativa ativa cobre.</summary>
    public static bool DefaultSelected(int type, string vendor, string name, PcRgbConfig cfg) =>
        NativeOwner(type, vendor, name, cfg) is null;

    public static string TypeName(int type) => type switch
    {
        TypeMotherboard => "Placa-mãe", TypeDram => "Memória RAM", TypeGpu => "Placa de vídeo", TypeCooler => "Cooler",
        TypeLedStrip => "Fita de LED / ARGB", TypeKeyboard => "Teclado", TypeMouse => "Mouse", TypeMousemat => "Mousepad",
        TypeHeadset => "Headset", TypeHeadsetStand => "Suporte de headset", TypeGamepad => "Controle", TypeLight => "Luminária",
        TypeSpeaker => "Caixa de som", TypeVirtual => "Virtual", TypeStorage => "Armazenamento", TypeCase => "Gabinete",
        TypeMicrophone => "Microfone", TypeAccessory => "Acessório", TypeKeypad => "Keypad",
        _ => $"tipo {type}",
    };
}

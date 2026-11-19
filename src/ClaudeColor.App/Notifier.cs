using System.Security;
using ClaudeColor.Core;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ClaudeColor.App;

/// <summary>
/// Notificações nativas do Windows (toast) para app sem pacote: registra um AppUserModelID em
/// HKCU\Software\Classes\AppUserModelId e usa o ToastNotifier dele. O clique traz o terminal da sessão à frente
/// (funciona enquanto o app está na bandeja, que é sempre o caso quando ele notifica).
/// </summary>
internal static class Notifier
{
    public const string AppId = "ClaudeColor";
    private const string Group = "claude";
    private static ToastNotifier? _notifier;
    private static readonly object Lock = new();

    /// <summary>Registra o AUMID (nome e ícone que aparecem na notificação). Idempotente.</summary>
    public static void Register()
    {
        try
        {
            var icon = Path.Combine(Storage.Directory, "icon.png");
            if (!File.Exists(icon))
            {
                Directory.CreateDirectory(Storage.Directory);
                using var bmp = Ui.SignalBitmap(Ui.Accent, 128);
                bmp.Save(icon, System.Drawing.Imaging.ImageFormat.Png);
            }
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
            key.SetValue("DisplayName", "Claude Color");
            key.SetValue("IconUri", icon);
            key.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Storage.Log($"notificação: registro falhou: {ex.Message}"); }
    }

    /// <summary>Remove as notificações do app e o registro do AppUserModelID (desinstalação).</summary>
    public static void Unregister()
    {
        try { ToastNotificationManager.History.Clear(AppId); } catch { /* nada mostrado ainda */ }
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{AppId}", throwOnMissingSubKey: false);
    }

    private static ToastNotifier Toasts()
    {
        lock (Lock)
        {
            if (_notifier is null)
            {
                Register();
                _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            }
            return _notifier;
        }
    }

    /// <summary>Notificações permitidas pelo Windows? (o usuário pode desligá-las em Configurações.)</summary>
    public static (bool Ok, string Text) Status()
    {
        try
        {
            return Toasts().Setting switch
            {
                NotificationSetting.Enabled => (true, "Permitidas"),
                NotificationSetting.DisabledForApplication => (false, "Desligadas para o Claude Color em Configurações → Sistema → Notificações"),
                NotificationSetting.DisabledForUser => (false, "Todas as notificações estão desligadas em Configurações → Sistema → Notificações"),
                NotificationSetting.DisabledByGroupPolicy => (false, "Bloqueadas por política do Windows"),
                NotificationSetting.DisabledByManifest => (false, "Bloqueadas pelo registro do app"),
                _ => (false, "Estado desconhecido"),
            };
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            // App sem pacote: o Windows só cria a entrada em Configurações depois da primeira notificação.
            return (true, "Permitidas (ainda nenhuma notificação mostrada)");
        }
        catch (Exception ex)
        {
            Storage.Log($"notificação: estado: {ex}");
            return (false, $"não foi possível consultar ({ex.GetType().Name} 0x{ex.HResult:X8})");
        }
    }

    /// <summary>Tag por sessão: uma notificação nova do mesmo projeto substitui a anterior.</summary>
    private static string Tag(SessionInfo s)
    {
        var key = s.Id ?? s.Project ?? (s.Window != 0 ? s.Window.ToString("X") : "claude");
        return key.Length > 60 ? key[..60] : key; // o Windows limita a tag a 64 caracteres
    }

    /// <summary>Mostra a notificação de um evento (ou a remove, para "trabalhando" e "você agiu").</summary>
    public static void Signal(AppConfig cfg, string evt, SessionInfo session)
    {
        try
        {
            if (evt is ClaudeEvents.Resumed or ClaudeEvents.Working)
            {
                ToastNotificationManager.History.Remove(Tag(session), Group, AppId);
                return;
            }
            if (!cfg.Events.TryGetValue(evt, out var sig) || !sig.UsesNotify(evt)) return;
            var busy = UserBusy.Current();
            if (cfg.Quiet.SkipNotify && UserBusy.IsBusy(busy))
            {
                Storage.Log($"notificação '{evt}' segurada ({UserBusy.Describe(busy)})");
                return;
            }
            Show(SignalsPage.DisplayName(evt), Body(evt, session), sig.Color, session, evt);
        }
        catch (Exception ex) { Storage.Log($"notificação '{evt}': {ex.Message}"); }
    }

    private static string Body(string evt, SessionInfo s)
    {
        var where = s.Project is { Length: > 0 } p ? $"Em {p}. " : "";
        return where + evt switch
        {
            ClaudeEvents.Done => "O Claude terminou.",
            ClaudeEvents.Idle => "O Claude está esperando você.",
            _ => "Clique para voltar ao terminal.",
        };
    }

    public static void Show(string title, string body, string colorHex, SessionInfo session, string evt = "test")
    {
        var icon = Path.Combine(Storage.Directory, $"signal-{colorHex.TrimStart('#')}.png");
        if (!File.Exists(icon))
        {
            Directory.CreateDirectory(Storage.Directory);
            using var bmp = Ui.SignalBitmap(Ui.ParseOr(colorHex, Ui.Accent), 96);
            bmp.Save(icon, System.Drawing.Imaging.ImageFormat.Png);
        }
        var args = $"window={session.Window}";
        var xml = $"""
            <toast launch="{SecurityElement.Escape(args)}">
              <visual>
                <binding template="ToastGeneric">
                  <text>{SecurityElement.Escape(title)}</text>
                  <text>{SecurityElement.Escape(body)}</text>
                  <image placement="appLogoOverride" hint-crop="circle" src="{SecurityElement.Escape(new Uri(icon).AbsoluteUri)}"/>
                </binding>
              </visual>
              <audio silent="{(evt == ClaudeEvents.Done ? "true" : "false")}"/>
            </toast>
            """;
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var toast = new ToastNotification(doc) { Tag = Tag(session), Group = Group, ExpirationTime = DateTimeOffset.Now.AddHours(1) };
        long window = session.Window;
        toast.Activated += (_, _) =>
        {
            Storage.Log($"notificação: clique, trazendo a janela {window:X} à frente");
            TaskbarFlash.BringToFront(window);
        };
        toast.Failed += (_, e) => Storage.Log($"notificação falhou: {e.ErrorCode?.Message}");
        Toasts().Show(toast);
    }
}

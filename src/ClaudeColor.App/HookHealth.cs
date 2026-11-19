using ClaudeColor.Core;
using Microsoft.Win32;

namespace ClaudeColor.App;

/// <summary>
/// Os hooks do Claude Code conseguem chegar até o app? Detecta o que já quebrou os sinais em silêncio:
/// hooks ausentes ou apontando para outro exe, e o exe marcado "Executar como administrador" (o Claude Code
/// roda sem administrador e não consegue abrir um exe que exige elevação).
/// </summary>
internal static class HookHealth
{
    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    public sealed record Problem(string Title, string Detail);

    /// <summary>Marca de compatibilidade que exige elevação para o exe (HKLM = todos os usuários, HKCU = só você).</summary>
    public static (bool AllUsers, string Value)? RunAsAdminMark(string exePath)
    {
        foreach (var (hive, allUsers) in new[] { (Registry.LocalMachine, true), (Registry.CurrentUser, false) })
        {
            try
            {
                using var key = hive.OpenSubKey(LayersKey);
                if (key?.GetValue(exePath) is string v &&
                    (v.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase) || v.Contains("RUNASHIGHEST", StringComparison.OrdinalIgnoreCase)))
                    return (allUsers, v);
            }
            catch { /* sem acesso ao registro: segue */ }
        }
        return null;
    }

    public static string FixRunAsAdmin(bool allUsers) =>
        "Clique com o botão direito no ClaudeColor.exe → Propriedades → Compatibilidade" +
        (allUsers ? " → Alterar configurações para todos os usuários" : "") +
        " → desmarque \"Executar este programa como administrador\". O app já roda como administrador pela tarefa agendada; o hook não precisa.";

    public static IReadOnlyList<Problem> Check()
    {
        var list = new List<Problem>();
        if (RunAsAdminMark(Program.ExePath) is { } mark)
            list.Add(new Problem("O Claude Code não consegue abrir o ClaudeColor.exe",
                "O exe está marcado \"Executar como administrador\" " + (mark.AllUsers ? "para todos os usuários" : "") + ". " + FixRunAsAdmin(mark.AllUsers)));
        switch (new HooksInstaller().GetStatus(Program.ExePath))
        {
            case HooksStatus.NotInstalled:
                list.Add(new Problem("Hooks do Claude Code não instalados", "Instale em Sistema → Hooks do Claude Code."));
                break;
            case HooksStatus.Outdated:
                list.Add(new Problem("Hooks apontam para outro executável", "Atualize em Sistema → Hooks do Claude Code."));
                break;
        }
        return list;
    }

    /// <summary>"há 3 min", "há 2 h", "nunca".</summary>
    public static string LastHookText()
    {
        if (Storage.LoadState().LastHookAt is not { } at) return "nenhum hook recebido ainda";
        var ago = DateTimeOffset.Now - at;
        var text = ago.TotalMinutes < 1 ? "agora há pouco"
            : ago.TotalHours < 1 ? $"há {(int)ago.TotalMinutes} min"
            : ago.TotalDays < 1 ? $"há {(int)ago.TotalHours} h"
            : $"em {at:dd/MM HH:mm}";
        return $"último hook {text}";
    }
}

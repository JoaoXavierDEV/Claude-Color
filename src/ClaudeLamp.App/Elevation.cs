using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using ClaudeLamp.Core;
using Microsoft.Win32;

namespace ClaudeLamp.App;

/// <summary>Execução como administrador e inicialização com o Windows (Tarefa Agendada elevada).</summary>
internal static class Elevation
{
    public const string TaskName = "ClaudeLamp";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ClaudeLamp";
    private const int ErrorCancelled = 1223; // usuário recusou o UAC

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Abre uma instância elevada (UAC). Retorna false se o usuário cancelou.</summary>
    public static bool StartElevated(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Program.ExePath, args) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { return false; }
    }

    /// <summary>Relança o app como admin; a nova instância espera esta fechar.</summary>
    public static bool RestartElevated() =>
        StartElevated($"--after {Environment.ProcessId}");

    public static bool AutostartTaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"", elevated: false) == 0;

    /// <summary>Cria (ou remove) a tarefa "ClaudeLamp" no logon com privilégios elevados.</summary>
    public static bool SetAutostartTask(bool enabled)
    {
        // A criação exige admin: se este processo não é elevado, delega para uma instância elevada.
        if (!IsElevated)
            return StartElevated(enabled ? "install-autostart" : "uninstall-autostart");

        if (!enabled) return RunSchtasks($"/Delete /TN \"{TaskName}\" /F", elevated: true) == 0;

        var xml = Path.Combine(Path.GetTempPath(), $"claudelamp-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xml, TaskXml(), System.Text.Encoding.Unicode);
            var ok = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F", elevated: true) == 0;
            if (ok) RemoveRunEntry();
            return ok;
        }
        finally { File.Delete(xml); }
    }

    /// <summary>A entrada antiga (sem admin) no Run do registro.</summary>
    public static void RemoveRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    public static bool RunEntryExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private static string TaskXml()
    {
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var exe = SecurityElement.Escape(Program.ExePath);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Claude Lamp: sinais do Claude Code na lâmpada e no RGB do PC.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
    }

    private static int RunSchtasks(string args, bool elevated)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            if (p.ExitCode != 0 && elevated) Storage.Log($"schtasks {args}: {err.Trim()}");
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Storage.Log($"schtasks: {ex.Message}");
            return -1;
        }
    }
}

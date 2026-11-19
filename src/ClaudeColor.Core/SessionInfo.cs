using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClaudeColor.Core;

/// <summary>
/// De onde veio o sinal: a janela do terminal (Windows Terminal, VS Code, console...) que roda a sessão
/// do Claude Code e o nome do projeto (pasta de trabalho).
/// </summary>
public sealed record SessionInfo(long Window, string? Project, string? Id = null)
{
    public static readonly SessionInfo None = new(0, null);

    /// <summary>"session_id" do JSON do hook (null se ausente). Só letras, números, '-' e '_'.</summary>
    public static string? IdFromHook(string hookJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(hookJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("session_id", out var id) && id.ValueKind == JsonValueKind.String)
                return CleanId(id.GetString());
        }
        catch (JsonException) { }
        return null;
    }

    public static string? CleanId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var clean = new string(id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return clean.Length == 0 ? null : clean.Length > 64 ? clean[..64] : clean;
    }

    /// <summary>Nome da pasta em "cwd" do JSON do hook.</summary>
    public static string? ProjectFromHook(string hookJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(hookJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String &&
                cwd.GetString() is { Length: > 0 } path)
            {
                var name = Path.GetFileName(path.TrimEnd('\\', '/'));
                return string.IsNullOrWhiteSpace(name) ? path : Clean(name);
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Sem quebras de linha e com tamanho limitado (vai no pipe e no texto da notificação).</summary>
    public static string Clean(string s)
    {
        var flat = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return flat.Length > 80 ? flat[..80] : flat;
    }

    /// <summary>Sessão do processo atual (o hook): janela do terminal pai e projeto do JSON.</summary>
    public static SessionInfo FromHook(string hookJson)
    {
        long window = 0;
        try { window = TerminalWindow.Find(); }
        catch (Exception ex) { Storage.Log($"sessão: janela não encontrada: {ex.Message}"); }
        return new SessionInfo(window, ProjectFromHook(hookJson), IdFromHook(hookJson));
    }
}

/// <summary>Acha a janela de nível superior do terminal que iniciou este processo.</summary>
public static class TerminalWindow
{
    /// <summary>Processos que não são "o terminal" mesmo tendo janelas (subir até eles daria a janela errada).</summary>
    private static readonly HashSet<string> NotTerminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "svchost", "services", "wininit", "winlogon", "sihost", "taskhostw", "RuntimeBroker", "ClaudeColor",
    };

    public static long Find()
    {
        // 1) Console clássico e Windows Terminal: a janela do console do processo pai.
        //    No Windows Terminal ela é uma pseudo-janela cujo dono é a janela do WT.
        if (AttachConsole(AttachParentProcess))
        {
            try
            {
                var console = GetConsoleWindow();
                if (console != IntPtr.Zero)
                {
                    var root = GetAncestor(console, GaRootOwner);
                    if (root != IntPtr.Zero && IsWindowVisible(root)) return root.ToInt64();
                    if (IsWindowVisible(console)) return console.ToInt64();
                }
            }
            finally { FreeConsole(); }
        }

        // 2) VS Code, JetBrains, wezterm...: sobe pelos processos pais até um com janela visível.
        var parents = ParentMap();
        int pid = Environment.ProcessId;
        for (int depth = 0; depth < 12 && parents.TryGetValue(pid, out var parent) && parent.Pid != 0; depth++)
        {
            pid = parent.Pid;
            if (!parents.TryGetValue(pid, out var self) || NotTerminals.Contains(Path.GetFileNameWithoutExtension(self.Exe))) break;
            if (TopLevelWindowOf(pid) is var hwnd and not 0) return hwnd;
        }
        return 0;
    }

    /// <summary>pid → (pai, exe) de todos os processos (uma foto do Toolhelp32).</summary>
    private static Dictionary<int, (int Pid, string Exe)> ParentMap()
    {
        var map = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == InvalidHandle) return map;
        try
        {
            var e = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                map[(int)e.ProcessId] = ((int)e.ParentProcessId, e.ExeFile);
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>A maior janela visível, sem dono e com título do processo (a "janela principal").</summary>
    private static long TopLevelWindowOf(int pid)
    {
        long best = 0;
        long bestArea = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || !IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero || GetWindowTextLengthW(hwnd) == 0)
                return true;
            GetWindowRect(hwnd, out var r);
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = hwnd.ToInt64(); }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // ───────────── Win32 ─────────────

    private const uint AttachParentProcess = unchecked((uint)-1);
    private const uint GaRootOwner = 3, GwOwner = 4, Th32csSnapProcess = 0x2;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect r);
}

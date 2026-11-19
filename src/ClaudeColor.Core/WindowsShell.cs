using System.Runtime.InteropServices;

namespace ClaudeColor.Core;

/// <summary>Pisca o botão de uma janela na barra de tarefas até ela vir para a frente.</summary>
public static class TaskbarFlash
{
    /// <returns>True se pediu para piscar (janela válida e fora de foco).</returns>
    public static bool Flash(long window)
    {
        var hwnd = new IntPtr(window);
        if (window == 0 || !IsWindow(hwnd) || GetForegroundWindow() == hwnd) return false;
        var info = new FlashWInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWInfo>(), Hwnd = hwnd, Flags = FlashwTray | FlashwTimerNoFg, Count = 0, Timeout = 0,
        };
        FlashWindowEx(ref info);
        return true;
    }

    /// <summary>Traz a janela para a frente (clique na notificação).</summary>
    public static void BringToFront(long window)
    {
        var hwnd = new IntPtr(window);
        if (window == 0 || !IsWindow(hwnd)) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
        var stop = new FlashWInfo { Size = (uint)Marshal.SizeOf<FlashWInfo>(), Hwnd = hwnd, Flags = FlashwStop };
        FlashWindowEx(ref stop);
    }

    private const uint FlashwStop = 0, FlashwTray = 0x2, FlashwTimerNoFg = 0xC;
    private const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags, Count, Timeout;
    }

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashWInfo info);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}

/// <summary>O Windows diz se o usuário está em tela cheia, apresentação ou horário silencioso.</summary>
public static class UserBusy
{
    public enum State { NotPresent = 1, Busy = 2, RunningD3DFullScreen = 3, PresentationMode = 4, AcceptsNotifications = 5, QuietTime = 6, App = 7 }

    public static State Current()
    {
        try { return SHQueryUserNotificationState(out var s) == 0 ? (State)s : State.AcceptsNotifications; }
        catch { return State.AcceptsNotifications; }
    }

    /// <summary>Tela cheia (jogo, vídeo), apresentação ou horário silencioso.</summary>
    public static bool IsBusy(State s) => s is State.Busy or State.RunningD3DFullScreen or State.PresentationMode or State.QuietTime;

    public static string Describe(State s) => s switch
    {
        State.Busy => "aplicativo em tela cheia",
        State.RunningD3DFullScreen => "jogo em tela cheia",
        State.PresentationMode => "modo de apresentação",
        State.QuietTime => "horário silencioso do Windows",
        _ => "disponível",
    };

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}

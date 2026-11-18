using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeLamp.Core;

/// <summary>
/// Canal entre o hook (processo curto, sem admin) e o app da bandeja (processo longo, possivelmente admin,
/// que segura o RGB do PC). Protocolo: uma linha "evento geração\n"; o app responde "ok\n".
/// </summary>
public static class SignalPipe
{
    public const string Name = "ClaudeLamp.Signals";

    public static string Format(string evt, long gen) => $"{evt} {gen}";

    public static bool TryParse(string? line, out string evt, out long gen)
    {
        evt = ""; gen = 0;
        var parts = line?.Trim().Split(' ');
        if (parts is not { Length: 2 } || !(ClaudeEvents.All.Contains(parts[0]) || parts[0] == ClaudeEvents.Resumed) || !long.TryParse(parts[1], out gen)) return false;
        evt = parts[0];
        return true;
    }

    /// <summary>
    /// Só o usuário atual (e o SYSTEM) acessam; rótulo de integridade MÉDIA para que o hook sem admin
    /// consiga escrever mesmo quando o app roda elevado (integridade alta).
    /// </summary>
    public static string SecurityDescriptor(string userSid) => $"D:(A;;GA;;;{userSid})(A;;GA;;;SY)S:(ML;;NW;;;ME)";

    /// <summary>Cria uma instância do servidor do pipe com o descritor de segurança acima.</summary>
    public static NamedPipeServerStream CreateServer(string name = Name)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("SID do usuário indisponível.");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(SecurityDescriptor(sid), 1, out var sd, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), SecurityDescriptor = sd };
            var handle = CreateNamedPipeW(@"\\.\pipe\" + name,
                PipeAccessDuplex | FileFlagOverlapped,
                PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
                MaxInstances: 4, OutBufferSize: 512, InBufferSize: 512, DefaultTimeout: 0, ref sa);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally { LocalFree(sd); }
    }

    /// <summary>Entrega o sinal ao app aberto. Retorna false se ninguém atendeu (app fechado).</summary>
    public static bool TrySend(string evt, long gen, int timeoutMs = 300, string name = Name)
    {
        try
        {
            // Sem CurrentUserOnly: quando o app roda como admin o dono do pipe é "Administradores";
            // o acesso é restringido pelo ACL do servidor.
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            var data = Encoding.UTF8.GetBytes(Format(evt, gen) + "\n");
            pipe.Write(data);
            pipe.Flush();
            var buf = new byte[8];
            var read = pipe.ReadAsync(buf).AsTask();
            return read.Wait(timeoutMs * 2) && Encoding.UTF8.GetString(buf, 0, read.Result).StartsWith("ok");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Storage.Log($"pipe: não entregue ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    // ───────────── Win32 ─────────────

    private const uint PipeAccessDuplex = 0x3, FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x0, PipeReadModeByte = 0x0, PipeWait = 0x0, PipeRejectRemoteClients = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr securityDescriptor, out uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint MaxInstances,
        uint OutBufferSize, uint InBufferSize, uint DefaultTimeout, ref SecurityAttributes sa);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}

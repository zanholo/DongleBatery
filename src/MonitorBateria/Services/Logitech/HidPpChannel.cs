using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MonitorBateria.Services.Logitech;

/// <summary>
/// Canal HID++ (relatórios "long", ID 0x11, 20 bytes) aberto sobre uma interface HID de um dongle/dispositivo Logitech.
/// Abre em modo compartilhado, então convive com o G HUB.
/// </summary>
internal sealed class HidPpChannel : IDisposable
{
    public const byte LongReport = 0x11;
    public const int LongLength = 20;
    private const byte SoftwareId = 0x0B; // usado no ping
    private int _swCounter;
    /// <summary>Código do último erro HID++ (0 = sem erro, -1 = timeout).</summary>
    public int LastError { get; private set; }

    // Cada requisição usa um "software id" diferente (2..15) para casar a resposta certa
    // mesmo quando um dispositivo lento responde atrasado.
    private byte NextSoftwareId() => (byte)(2 + (Interlocked.Increment(ref _swCounter) % 14));

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    private readonly FileStream _stream;
    private readonly SafeFileHandle _handle;
    private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Product { get; }
    public static System.Collections.Generic.List<string>? Trace;

    private HidPpChannel(SafeFileHandle handle)
    {
        _handle = handle;
        _stream = new FileStream(handle, FileAccess.ReadWrite, 1, true);
        Product = ReadProduct(handle);
        _ = Task.Run(ReadLoop);
    }

    public static HidPpChannel? Open(string path)
    {
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        try { return new HidPpChannel(h); }
        catch { h.Dispose(); return null; }
    }

    private static string ReadProduct(SafeFileHandle h)
    {
        var buf = new byte[256];
        return HidD_GetProductString(h, buf, buf.Length)
            ? Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim()
            : "";
    }

    private async Task ReadLoop()
    {
        var buf = new byte[64];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buf.AsMemory(0, LongLength), _cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                var msg = buf.AsSpan(0, n).ToArray();
                Trace?.Add("  <- " + Convert.ToHexString(msg));
                _inbox.Writer.TryWrite(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Trace?.Add("  read: " + ex.GetType().Name + " " + ex.Message); }
        _inbox.Writer.TryComplete();
    }

    /// <summary>
    /// Envia uma requisição HID++ 2.0 e espera a resposta correspondente.
    /// Retorna os parâmetros da resposta (16 bytes) ou null em erro/timeout.
    /// </summary>
    public async Task<byte[]?> RequestAsync(byte device, byte feature, byte function, byte[]? args = null, int timeoutMs = 1200)
    {
        // Dispositivos sem fio em modo de economia às vezes demoram a responder: tenta de novo uma vez.
        var r = await RequestOnceAsync(device, feature, function, args, timeoutMs).ConfigureAwait(false);
        if (r is null && LastError == -1)
            r = await RequestOnceAsync(device, feature, function, args, timeoutMs * 2).ConfigureAwait(false);
        return r;
    }

    private async Task<byte[]?> RequestOnceAsync(byte device, byte feature, byte function, byte[]? args, int timeoutMs)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        LastError = -1;
        try
        {
            // Sem limpar a fila: respostas atrasadas de pedidos anteriores têm outro software id e são ignoradas.

            byte fs = (byte)((function << 4) | NextSoftwareId());
            var req = new byte[LongLength];
            req[0] = LongReport;
            req[1] = device;
            req[2] = feature;
            req[3] = fs;
            if (args != null) Array.Copy(args, 0, req, 4, Math.Min(args.Length, LongLength - 4));

            using var timeout = new CancellationTokenSource(timeoutMs);
            Trace?.Add("  -> " + Convert.ToHexString(req));
            await _stream.WriteAsync(req, timeout.Token).ConfigureAwait(false);

            while (true)
            {
                var r = await _inbox.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (r.Length < 5 || r[1] != device) continue;
                if (r[0] == LongReport && r[2] == feature && r[3] == fs)
                {
                    LastError = 0;
                    return r.AsSpan(4).ToArray();
                }
                // Erro HID++ 2.0: [0x11, dev, 0xFF, feature, fs, código] / HID++ 1.0: [.., 0x8F, ..]
                if ((r[2] == 0xFF || r[2] == 0x8F) && r[3] == feature && r[4] == fs)
                {
                    LastError = r.Length > 5 ? r[5] : 1;
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) Trace?.Add("  req: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Manda um ping para vários índices de uma vez e espera as respostas (dispositivos dormindo
    /// podem levar 1–2 s para acordar). Retorna os índices que responderam.
    /// </summary>
    public async Task<System.Collections.Generic.HashSet<byte>> PingAsync(byte[] indices, int timeoutMs)
    {
        var alive = new System.Collections.Generic.HashSet<byte>();
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            while (_inbox.Reader.TryRead(out _)) { }
            byte fs = (byte)((0x1 << 4) | SoftwareId);
            using var timeout = new CancellationTokenSource(timeoutMs);
            foreach (var idx in indices)
            {
                var req = new byte[LongLength];
                req[0] = LongReport; req[1] = idx; req[2] = 0x00; req[3] = fs; req[6] = 0x5A;
                Trace?.Add("  -> " + Convert.ToHexString(req));
                await _stream.WriteAsync(req, timeout.Token).ConfigureAwait(false);
            }
            while (alive.Count < indices.Length)
            {
                var r = await _inbox.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (r.Length >= 4 && r[0] == LongReport && r[2] == 0x00 && r[3] == fs) alive.Add(r[1]);
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) Trace?.Add("  ping: " + ex.GetType().Name + " " + ex.Message);
        }
        finally { _lock.Release(); }
        return alive;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _stream.Dispose(); } catch { }
        _handle.Dispose();
    }
}

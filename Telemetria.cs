using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MrsChatServer;

/// <summary>
/// Números do painel: tráfego, mensagens, CPU e memória (do app e do sistema),
/// além do histórico recente de tráfego cifrado e do registro de eventos.
/// </summary>
static class Telemetria
{
    public readonly record struct Trafego(DateTime Hora, string Autor, string Destino, byte[] Amostra, int Bytes, bool Cifrado);
    public readonly record struct Evento(DateTime Hora, string Texto, ConsoleColor? Cor);
    public readonly record struct Amostra(
        double CpuApp, double? CpuSistema, long RamApp, long HeapGc, long? RamSistemaUsada, long? RamSistemaTotal,
        int Threads, double RxPorSeg, double TxPorSeg);

    public const int TamanhoHistorico = 120;

    private static long _rx, _tx, _mensagens;
    private static readonly object Trava = new();
    private static readonly LinkedList<Trafego> _trafego = new();
    private static readonly LinkedList<Evento> _eventos = new();
    private static readonly Queue<double> _histTrafego = new();
    private static readonly Queue<double> _histCpu = new();
    private static Amostra _atual;

    public static DateTime Inicio { get; } = DateTime.Now;
    public static long BytesRecebidos => Interlocked.Read(ref _rx);
    public static long BytesEnviados => Interlocked.Read(ref _tx);
    public static long Mensagens => Interlocked.Read(ref _mensagens);

    public static void SomarRecebidos(int n) => Interlocked.Add(ref _rx, n);
    public static void SomarEnviados(int n) => Interlocked.Add(ref _tx, n);

    /// <summary>Guarda uma mensagem como passou pela rede (cifrada). O texto em claro nunca vem para cá.</summary>
    public static void RegistrarTrafego(string autor, string destino, string? linhaCifrada, int bytesClaros)
    {
        Interlocked.Increment(ref _mensagens);
        byte[] amostra;
        int bytes;
        bool cifrado = linhaCifrada is not null;
        if (cifrado)
        {
            try { amostra = Convert.FromBase64String(linhaCifrada!); } catch (FormatException) { amostra = []; }
            bytes = amostra.Length;
            if (amostra.Length > 48) amostra = amostra[..48];
        }
        else
        {
            amostra = []; // cliente sem criptografia: não mostramos nem os bytes, só o tamanho
            bytes = bytesClaros;
        }
        lock (Trava)
        {
            _trafego.AddLast(new Trafego(DateTime.Now, autor, destino, amostra, bytes, cifrado));
            while (_trafego.Count > 200) _trafego.RemoveFirst();
        }
    }

    public static void RegistrarEvento(string texto, ConsoleColor? cor)
    {
        lock (Trava)
        {
            _eventos.AddLast(new Evento(DateTime.Now, texto, cor));
            while (_eventos.Count > 500) _eventos.RemoveFirst();
        }
    }

    public static List<Trafego> TrafegoRecente(int n) { lock (Trava) return _trafego.TakeLast(n).ToList(); }
    public static List<Evento> EventosRecentes(int n) { lock (Trava) return _eventos.TakeLast(n).ToList(); }
    public static Amostra Atual { get { lock (Trava) return _atual; } }
    public static double[] HistoricoTrafego { get { lock (Trava) return [.. _histTrafego]; } }
    public static double[] HistoricoCpu { get { lock (Trava) return [.. _histCpu]; } }

    // ------------------------------------------------------------ amostragem

    /// <summary>Atualiza CPU, memória e taxas de tráfego uma vez por segundo.</summary>
    public static async Task AmostrarAsync(CancellationToken ct)
    {
        var proc = Process.GetCurrentProcess();
        TimeSpan cpuAnterior = proc.TotalProcessorTime;
        var relogio = Stopwatch.StartNew();
        double tAnterior = 0;
        long rxAnterior = BytesRecebidos, txAnterior = BytesEnviados;
        var cpuSistAnterior = LerCpuSistema();

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }

            proc.Refresh();
            double t = relogio.Elapsed.TotalSeconds, dt = Math.Max(0.001, t - tAnterior);
            TimeSpan cpu = proc.TotalProcessorTime;
            double cpuApp = (cpu - cpuAnterior).TotalSeconds / dt / Environment.ProcessorCount * 100;
            cpuAnterior = cpu; tAnterior = t;

            var cpuSist = LerCpuSistema();
            double? cpuSistema = null;
            if (cpuSist is { } agora && cpuSistAnterior is { } antes && agora.total > antes.total)
                cpuSistema = 100.0 * (agora.ocupado - antes.ocupado) / (agora.total - antes.total);
            cpuSistAnterior = cpuSist;

            long rx = BytesRecebidos, tx = BytesEnviados;
            double rxs = (rx - rxAnterior) / dt, txs = (tx - txAnterior) / dt;
            rxAnterior = rx; txAnterior = tx;

            var (usada, total) = LerMemoriaSistema();
            var a = new Amostra(Math.Clamp(cpuApp, 0, 100), cpuSistema is { } c ? Math.Clamp(c, 0, 100) : null,
                proc.WorkingSet64, GC.GetTotalMemory(false), usada, total, proc.Threads.Count, rxs, txs);

            lock (Trava)
            {
                _atual = a;
                _histTrafego.Enqueue(rxs + txs);
                _histCpu.Enqueue(a.CpuApp);
                while (_histTrafego.Count > TamanhoHistorico) _histTrafego.Dequeue();
                while (_histCpu.Count > TamanhoHistorico) _histCpu.Dequeue();
            }
        }
    }

    /// <summary>Tempo de CPU ocupado/total do sistema (Linux: /proc/stat; Windows: GetSystemTimes).</summary>
    private static (ulong ocupado, ulong total)? LerCpuSistema()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                string? l = File.ReadLines("/proc/stat").FirstOrDefault();
                if (l is null || !l.StartsWith("cpu ")) return null;
                ulong[] v = l[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(ulong.Parse).ToArray();
                ulong total = v.Take(8).Aggregate(0UL, (s, x) => s + x);
                ulong ocioso = v[3] + (v.Length > 4 ? v[4] : 0);
                return (total - ocioso, total);
            }
            if (OperatingSystem.IsWindows() && Win.GetSystemTimes(out long parado, out long kernel, out long usuario))
                return ((ulong)(kernel + usuario - parado), (ulong)(kernel + usuario)); // tempo de kernel já inclui o ocioso
        }
        catch (Exception) { }
        return null;
    }

    private static (long? usada, long? total) LerMemoriaSistema()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                long total = 0, disponivel = 0;
                foreach (var l in File.ReadLines("/proc/meminfo"))
                {
                    if (l.StartsWith("MemTotal:")) total = KbDe(l);
                    else if (l.StartsWith("MemAvailable:")) disponivel = KbDe(l);
                }
                if (total > 0) return (total - disponivel, total);
            }
            else if (OperatingSystem.IsWindows())
            {
                var s = new Win.MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<Win.MemoryStatusEx>() };
                if (Win.GlobalMemoryStatusEx(ref s)) return ((long)(s.ullTotalPhys - s.ullAvailPhys), (long)s.ullTotalPhys);
            }
        }
        catch (Exception) { }
        long t = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return (null, t > 0 ? t : null);
    }

    private static long KbDe(string linha) =>
        long.Parse(linha.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;

    private static class Win
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetSystemTimes(out long ocioso, out long kernel, out long usuario);
    }
}

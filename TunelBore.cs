using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MrsChatServer;

/// <summary>
/// Cliente do protocolo do bore (https://github.com/ekzhang/bore), implementado direto em C#,
/// sem precisar instalar nada.
///
/// Como funciona: o servidor abre uma conexão de SAÍDA até o relay (por padrão bore.pub:7835),
/// e o relay passa a aceitar conexões numa porta pública (ex.: bore.pub:41234) e repassá-las
/// para cá. Como a conexão parte de dentro, funciona atrás de NAT e CGNAT sem abrir porta
/// no roteador.
///
/// Protocolo: mensagens JSON terminadas em byte 0 na porta de controle.
///   cliente → relay: {"Hello":porta} | {"Accept":"uuid"} | {"Authenticate":"hmac-hex"}
///   relay → cliente: {"Hello":porta} | "Heartbeat" | {"Connection":"uuid"} | {"Challenge":"uuid"} | {"Error":"msg"}
/// </summary>
sealed class TunelBore
{
    private const int TamanhoMaximoQuadro = 256;
    private static readonly TimeSpan TempoRede = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TempoSemHeartbeat = TimeSpan.FromSeconds(30);

    private readonly string _relayHost;
    private readonly int _relayPorta;
    private readonly int _portaLocal;
    private readonly byte[]? _chaveSegredo;
    private int _portaDesejada;

    public string? EnderecoPublico { get; private set; }
    public event Action<string>? EnderecoDisponivel;

    public TunelBore(string relayHost, int relayPorta, int portaLocal, string? segredo, int portaDesejada)
    {
        _relayHost = relayHost;
        _relayPorta = relayPorta;
        _portaLocal = portaLocal;
        _portaDesejada = portaDesejada;
        // O bore usa SHA-256(segredo) como chave do HMAC.
        _chaveSegredo = segredo is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(segredo));
    }

    /// <summary>Mantém o túnel aberto, reconectando com espera crescente se a conexão cair.</summary>
    public async Task ExecutarAsync(CancellationToken ct)
    {
        var espera = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SessaoDeControleAsync(ct);
                espera = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ErroDoRelay ex) when (ex.Fatal)
            {
                Log.Erro($"Túnel público: {ex.Message}");
                Log.Erro("O acesso pela rede local continua funcionando.");
                return;
            }
            catch (Exception ex)
            {
                Log.Aviso($"Túnel público indisponível ({ex.Message}). Tentando de novo em {espera.TotalSeconds:0}s...");
            }

            EnderecoPublico = null;
            try { await Task.Delay(espera, ct); } catch (OperationCanceledException) { return; }
            espera = TimeSpan.FromSeconds(Math.Min(espera.TotalSeconds * 2, 60));
        }
    }

    private async Task SessaoDeControleAsync(CancellationToken ct)
    {
        using var tcp = await ConectarRelayAsync(ct);
        var stream = tcp.GetStream();

        await AutenticarAsync(stream, ct);
        await EnviarAsync(stream, new Dictionary<string, object> { ["Hello"] = _portaDesejada }, ct);

        using (var resposta = await ReceberAsync(stream, TempoRede, ct))
        {
            var raiz = resposta.RootElement;
            if (raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("Hello", out var p))
            {
                int porta = p.GetInt32();
                _portaDesejada = porta; // ao reconectar, tenta manter o mesmo endereço
                EnderecoPublico = $"{_relayHost}:{porta}";
                EnderecoDisponivel?.Invoke(EnderecoPublico);
            }
            else if (raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("Error", out var e))
            {
                string msg = e.GetString() ?? "erro desconhecido";
                // Porta pedida ocupada depois de uma queda: na próxima tentativa aceita qualquer uma.
                if (_portaDesejada != 0 && msg.Contains("port", StringComparison.OrdinalIgnoreCase))
                {
                    _portaDesejada = 0;
                    throw new ErroDoRelay($"o relay recusou a porta pedida ({msg})", fatal: false);
                }
                throw new ErroDoRelay($"o relay recusou a conexão: {msg}", fatal: true);
            }
            else if (raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("Challenge", out _))
            {
                throw new ErroDoRelay("este relay exige segredo. Use --relay-segredo.", fatal: true);
            }
            else
            {
                throw new ErroDoRelay($"resposta inesperada do relay: {raiz.GetRawText()}", fatal: false);
            }
        }

        while (true)
        {
            using var msg = await ReceberAsync(stream, TempoSemHeartbeat, ct);
            var raiz = msg.RootElement;

            if (raiz.ValueKind == JsonValueKind.String) continue; // "Heartbeat"

            if (raiz.TryGetProperty("Connection", out var id))
            {
                string uuid = id.GetString() ?? throw new ErroDoRelay("Connection sem id", false);
                _ = Task.Run(() => AceitarConexaoAsync(uuid, ct), CancellationToken.None);
            }
            else if (raiz.TryGetProperty("Error", out var e))
            {
                Log.Aviso($"Túnel público: o relay informou um erro: {e.GetString()}");
            }
        }
    }

    /// <summary>O relay avisou que alguém conectou na porta pública: abre uma conexão nova e emenda com o servidor local.</summary>
    private async Task AceitarConexaoAsync(string uuid, CancellationToken ct)
    {
        try
        {
            using var remoto = await ConectarRelayAsync(ct);
            var remotoStream = remoto.GetStream();
            await AutenticarAsync(remotoStream, ct);
            await EnviarAsync(remotoStream, new Dictionary<string, object> { ["Accept"] = uuid }, ct);

            using var local = new TcpClient { NoDelay = true };
            await local.ConnectAsync("127.0.0.1", _portaLocal, ct);
            var localStream = local.GetStream();

            using var fim = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var ida = remotoStream.CopyToAsync(localStream, fim.Token);
            var volta = localStream.CopyToAsync(remotoStream, fim.Token);
            await Task.WhenAny(ida, volta);
            fim.Cancel();
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Aviso($"Túnel público: falha ao aceitar conexão ({ex.Message}).");
        }
    }

    private async Task<TcpClient> ConectarRelayAsync(CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        prazo.CancelAfter(TempoRede);
        try
        {
            await tcp.ConnectAsync(_relayHost, _relayPorta, prazo.Token);
            return tcp;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"sem resposta de {_relayHost}:{_relayPorta}");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Se houver segredo, o relay manda um desafio (UUID) e esperamos HMAC-SHA256(chave, bytes do UUID).</summary>
    private async Task AutenticarAsync(NetworkStream stream, CancellationToken ct)
    {
        if (_chaveSegredo is null) return;

        using var desafio = await ReceberAsync(stream, TempoRede, ct);
        var raiz = desafio.RootElement;
        if (raiz.ValueKind != JsonValueKind.Object || !raiz.TryGetProperty("Challenge", out var c))
            throw new ErroDoRelay("o relay não pediu autenticação; confira se --relay-segredo é necessário.", fatal: true);

        byte[] bytesUuid = Convert.FromHexString((c.GetString() ?? "").Replace("-", ""));
        string resposta = Convert.ToHexString(HMACSHA256.HashData(_chaveSegredo, bytesUuid)).ToLowerInvariant();
        await EnviarAsync(stream, new Dictionary<string, object> { ["Authenticate"] = resposta }, ct);
    }

    private static async Task EnviarAsync(NetworkStream stream, object mensagem, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(mensagem);
        byte[] quadro = new byte[json.Length + 1];
        json.CopyTo(quadro, 0); // último byte já é 0 (delimitador)
        await stream.WriteAsync(quadro, ct);
    }

    /// <summary>Lê um quadro byte a byte até o 0, para não consumir dados que vêm depois dele.</summary>
    private static async Task<JsonDocument> ReceberAsync(NetworkStream stream, TimeSpan prazo, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(prazo);
        var buffer = new byte[TamanhoMaximoQuadro + 1];
        var um = new byte[1];
        int n = 0;
        try
        {
            while (true)
            {
                int lidos = await stream.ReadAsync(um, cts.Token);
                if (lidos == 0) throw new IOException("o relay fechou a conexão");
                if (um[0] == 0)
                {
                    if (n == 0) continue;
                    return JsonDocument.Parse(buffer.AsMemory(0, n));
                }
                if (n == TamanhoMaximoQuadro) throw new InvalidDataException("mensagem do relay grande demais");
                buffer[n++] = um[0];
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("o relay parou de responder");
        }
    }

    private sealed class ErroDoRelay(string mensagem, bool fatal) : Exception(mensagem)
    {
        public bool Fatal { get; } = fatal;
    }
}

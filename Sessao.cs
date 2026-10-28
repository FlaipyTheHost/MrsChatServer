using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace MrsChatServer;

/// <summary>
/// Um cliente conectado. Cada sessão tem sua própria fila de saída, então um cliente
/// lento ou travado não atrasa as mensagens dos outros. Quando há <see cref="Canal"/>,
/// toda linha é cifrada na saída e decifrada na entrada; o resto do servidor só vê texto.
/// </summary>
sealed class Sessao : IDisposable
{
    private static int _contador;
    private static readonly UTF8Encoding Utf8SemBom = new(false);

    private readonly record struct Saida(string Linha, bool Cifrar);

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly Channel<Saida> _saida = Channel.CreateBounded<Saida>(
        new BoundedChannelOptions(500) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _token;
    private readonly Task _escritor;
    private int _encerrada;
    private string? _pendente;
    private long _rx, _tx;

    public int Id { get; } = Interlocked.Increment(ref _contador);
    public string Nome { get; set; } = "";
    public string Endereco { get; }
    public DateTime ConectadoEm { get; } = DateTime.Now;
    public CancellationToken Token => _token;
    public CanalSeguro? Canal { get; private set; }
    public bool Cifrada => Canal is not null;
    public int MensagensEnviadas;
    public long BytesRecebidos => Interlocked.Read(ref _rx);
    public long BytesEnviados => Interlocked.Read(ref _tx);

    /// <summary>A última linha como chegou pela rede (base64 cifrado), para o painel mostrar sem decifrar.</summary>
    public string? UltimaLinhaCifrada { get; private set; }

    private readonly LeitorLinhas _leitor;

    public Sessao(TcpClient tcp, CancellationToken ctServidor)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = new StreamContador(tcp.GetStream(), n => { Interlocked.Add(ref _rx, n); Telemetria.SomarRecebidos(n); });
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ctServidor);
        _token = _cts.Token;
        Endereco = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        // Linhas cifradas são base64: cabem ~3/4 disso de texto, e o limite de texto é checado depois.
        _leitor = new LeitorLinhas(_stream, Protocolo.TamanhoMaximoLinha * 2);
        _escritor = Task.Run(EscreverAsync);
    }

    /// <summary>Lê a linha seguinte (já decifrada). null = conexão fechada.</summary>
    public async Task<string?> LerLinhaAsync(CancellationToken ct)
    {
        if (_pendente is { } p) { _pendente = null; return p; }

        string? bruta = await _leitor.LerLinhaAsync(ct);
        if (bruta is null) return null;
        if (Canal is null)
        {
            if (bruta.Length > Protocolo.TamanhoMaximoLinha)
                throw new InvalidDataException($"Linha maior que {Protocolo.TamanhoMaximoLinha} caracteres.");
            return bruta;
        }

        UltimaLinhaCifrada = bruta;
        try
        {
            string claro = Canal.Decifrar(bruta);
            if (claro.Length > Protocolo.TamanhoMaximoLinha)
                throw new InvalidDataException($"Linha maior que {Protocolo.TamanhoMaximoLinha} caracteres.");
            return claro;
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("Linha cifrada inválida (alterada, repetida ou fora de ordem).");
        }
    }

    /// <summary>Lê a 1ª linha sem decifrar (para ver se o cliente quer criptografia).</summary>
    public Task<string?> LerLinhaBrutaAsync(CancellationToken ct) => _leitor.LerLinhaAsync(ct);

    /// <summary>Devolve uma linha para ser lida de novo (cliente antigo: a 1ª linha já era o nome).</summary>
    public void Devolver(string linha) => _pendente = linha;

    /// <summary>
    /// Envia a resposta do aperto de mãos em texto puro e liga a criptografia para tudo o que vier depois.
    /// A fila preserva a ordem, então nenhuma mensagem cifrada sai antes da resposta.
    /// </summary>
    public void AtivarCanal(CanalSeguro canal, string respostaEmClaro)
    {
        _saida.Writer.TryWrite(new Saida(respostaEmClaro, Cifrar: false));
        Canal = canal;
    }

    /// <summary>Enfileira uma linha. Se a fila do cliente estiver cheia, ele é desconectado.</summary>
    public void Enviar(string linha)
    {
        if (Volatile.Read(ref _encerrada) != 0) return;
        if (!_saida.Writer.TryWrite(new Saida(linha, Cifrar: Canal is not null)))
        {
            Log.Aviso($"{Descricao}: fila de saída cheia, desconectando.");
            Encerrar();
        }
    }

    /// <summary>Envia o que estiver na fila e então fecha a conexão.</summary>
    public async Task EncerrarAposEnviarAsync(TimeSpan limite)
    {
        _saida.Writer.TryComplete();
        await Task.WhenAny(_escritor, Task.Delay(limite));
        Encerrar();
    }

    public void Encerrar()
    {
        if (Interlocked.Exchange(ref _encerrada, 1) != 0) return;
        _saida.Writer.TryComplete();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _tcp.Close(); } catch { }
    }

    public string Descricao => string.IsNullOrEmpty(Nome) ? $"#{Id} ({Endereco})" : $"{Nome} ({Endereco})";

    private async Task EscreverAsync()
    {
        try
        {
            // Um único escritor: a cifragem acontece aqui, na mesma ordem em que os bytes saem,
            // para o contador (nonce) do outro lado bater.
            await foreach (var s in _saida.Reader.ReadAllAsync(_token))
            {
                string linha = s.Cifrar ? Canal!.Cifrar(s.Linha) : s.Linha;
                byte[] dados = Utf8SemBom.GetBytes(linha + "\n");
                await _stream.WriteAsync(dados, _token);
                Interlocked.Add(ref _tx, dados.Length);
                Telemetria.SomarEnviados(dados.Length);
            }
        }
        catch (Exception) when (_token.IsCancellationRequested || !_tcp.Connected) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            // Falha de escrita: derruba a conexão para o laço de leitura também terminar.
            if (!_saida.Reader.Completion.IsCompleted) Encerrar();
        }
    }

    public void Dispose()
    {
        Encerrar();
        Canal?.Dispose();
        _cts.Dispose();
    }

    /// <summary>Repassa leituras e escritas, contando os bytes recebidos (as escritas são contadas no escritor).</summary>
    private sealed class StreamContador(Stream interno, Action<int> aoLer) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => interno.Flush();
        public override int Read(byte[] buffer, int offset, int count) { int n = interno.Read(buffer, offset, count); aoLer(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = await interno.ReadAsync(buffer, ct);
            aoLer(n);
            return n;
        }
        public override void Write(byte[] buffer, int offset, int count) => interno.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => interno.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

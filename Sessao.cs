using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace MrsChatServer;

/// <summary>
/// Um cliente conectado. Cada sessão tem sua própria fila de saída, então um cliente
/// lento ou travado não atrasa as mensagens dos outros (na v1 o envio era sequencial).
/// </summary>
sealed class Sessao : IDisposable
{
    private static int _contador;
    private static readonly UTF8Encoding Utf8SemBom = new(false);

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Channel<string> _saida = Channel.CreateBounded<string>(
        new BoundedChannelOptions(500) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _token;
    private readonly Task _escritor;
    private int _encerrada;

    public int Id { get; } = Interlocked.Increment(ref _contador);
    public string Nome { get; set; } = "";
    public string Endereco { get; }
    public DateTime ConectadoEm { get; } = DateTime.Now;
    public LeitorLinhas Leitor { get; }
    public CancellationToken Token => _token;

    public Sessao(TcpClient tcp, CancellationToken ctServidor)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ctServidor);
        _token = _cts.Token;
        Endereco = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        Leitor = new LeitorLinhas(_stream, Protocolo.TamanhoMaximoLinha);
        _escritor = Task.Run(EscreverAsync);
    }

    /// <summary>Enfileira uma linha. Se a fila do cliente estiver cheia, ele é desconectado.</summary>
    public void Enviar(string linha)
    {
        if (Volatile.Read(ref _encerrada) != 0) return;
        if (!_saida.Writer.TryWrite(linha))
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
            await foreach (var linha in _saida.Reader.ReadAllAsync(_token))
            {
                byte[] dados = Utf8SemBom.GetBytes(linha + "\n");
                await _stream.WriteAsync(dados, _token);
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
        _cts.Dispose();
    }
}

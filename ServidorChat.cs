using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MrsChatServer;

sealed class ServidorChat
{
    private static readonly TimeSpan TempoParaNome = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TempoParaSenha = TimeSpan.FromSeconds(90);

    private readonly OpcoesServidor _op;
    private readonly ConcurrentDictionary<int, Sessao> _sessoes = new();
    private readonly object _travaNomes = new();

    public ServidorChat(OpcoesServidor op) => _op = op;

    public int Conectados => _sessoes.Values.Count(s => s.Nome.Length > 0);

    public async Task ExecutarAsync(CancellationToken ct)
    {
        TcpListener listener = CriarListener();
        listener.Start();
        Log.Sucesso($"Servidor aguardando clientes na porta {_op.Porta}...");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => TratarConexaoAsync(tcp, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }

    private TcpListener CriarListener()
    {
        // Escuta em IPv6 e IPv4 ao mesmo tempo quando possível (na v1 "localhost" via ::1 falhava).
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                var l = new TcpListener(IPAddress.IPv6Any, _op.Porta);
                l.Server.DualMode = true;
                return l;
            }
            catch (SocketException) { }
        }
        return new TcpListener(IPAddress.Any, _op.Porta);
    }

    private async Task TratarConexaoAsync(TcpClient tcp, CancellationToken ctServidor)
    {
        using var s = new Sessao(tcp, ctServidor);
        bool entrou = false;
        try
        {
            if (Conectados >= _op.MaxClientes)
            {
                s.Enviar(Protocolo.PrefixoServidor + "A sala está cheia. Tente mais tarde.");
                await s.EncerrarAposEnviarAsync(TimeSpan.FromSeconds(2));
                return;
            }

            if (!await AutenticarAsync(s)) return;

            entrou = true;
            Log.Sucesso($"Cliente conectado: {s.Descricao}");
            s.Enviar($"{Protocolo.PrefixoServidor}Bem-vindo, {s.Nome}! {Conectados} usuário(s) online. Digite /ajuda para ver os comandos.");
            s.Enviar(Protocolo.PrefixoSeuNome + s.Nome);
            s.Enviar(Protocolo.PrefixoSala + _op.Sala);
            Divulgar($"{s.Nome} conectou-se.", s); // mesmo texto da v1

            while (true)
            {
                string? linha = await s.Leitor.LerLinhaAsync(s.Token);
                if (linha is null) break;
                linha = LimparTexto(linha).Trim();
                if (linha.Length == 0) continue;

                if (linha.Equals("exit", StringComparison.OrdinalIgnoreCase)) break; // v1

                if (linha.StartsWith('/'))
                {
                    if (!ExecutarComando(s, linha)) break;
                    continue;
                }

                string formatada = $"{s.Nome}: {linha}";
                Log.Chat(formatada);
                Divulgar(formatada, s);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException ex)
        {
            Log.Aviso($"{s.Descricao}: {ex.Message} Desconectando.");
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (Exception ex)
        {
            Log.Erro($"Erro com {s.Descricao}: {ex.Message}");
        }
        finally
        {
            _sessoes.TryRemove(s.Id, out _);
            s.Encerrar();
            if (entrou)
            {
                Log.Info($"Cliente desconectado: {s.Descricao}");
                Divulgar($"{s.Nome} desconectou.", s);
            }
        }
    }

    /// <summary>Lê o nome (1ª linha, igual à v1) e, se configurado, a senha.</summary>
    private async Task<bool> AutenticarAsync(Sessao s)
    {
        string? nome = await LerComPrazoAsync(s, TempoParaNome);
        if (nome is null) return false;

        string? erro = ValidarNome(nome = NormalizarNome(nome));
        if (erro is not null)
        {
            s.Enviar(Protocolo.PrefixoServidor + erro);
            await s.EncerrarAposEnviarAsync(TimeSpan.FromSeconds(2));
            Log.Aviso($"Conexão recusada de {s.Endereco}: {erro}");
            return false;
        }

        if (_op.Senha is { } senhaSala)
        {
            s.Enviar(Protocolo.PedidoSenha);
            string? tentativa = await LerComPrazoAsync(s, TempoParaSenha);
            if (tentativa is null) return false;

            if (!SenhaConfere(tentativa.Trim(), senhaSala))
            {
                await Task.Delay(1000); // atrasa tentativas de força bruta
                s.Enviar(Protocolo.PrefixoServidor + "Senha incorreta.");
                await s.EncerrarAposEnviarAsync(TimeSpan.FromSeconds(2));
                Log.Aviso($"Senha incorreta de {s.Endereco} (nome: {nome}).");
                return false;
            }
        }

        lock (_travaNomes)
        {
            s.Nome = NomeDisponivel(nome);
            _sessoes[s.Id] = s;
        }
        return true;
    }

    private static async Task<string?> LerComPrazoAsync(Sessao s, TimeSpan prazo)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(s.Token);
        cts.CancelAfter(prazo);
        try
        {
            return await s.Leitor.LerLinhaAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!s.Token.IsCancellationRequested)
        {
            return null; // demorou demais
        }
    }

    private static bool SenhaConfere(string tentativa, string senha) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(tentativa)),
            SHA256.HashData(Encoding.UTF8.GetBytes(senha)));

    // ----------------------------------------------------------------- comandos

    /// <returns>false se o cliente deve ser desconectado.</returns>
    private bool ExecutarComando(Sessao s, string linha)
    {
        int espaco = linha.IndexOf(' ');
        string cmd = (espaco < 0 ? linha : linha[..espaco]).ToLowerInvariant();
        string resto = espaco < 0 ? "" : linha[(espaco + 1)..].Trim();

        switch (cmd)
        {
            case "/ajuda" or "/help" or "/?":
                foreach (var l in AjudaComandos) s.Enviar(Protocolo.PrefixoServidor + l);
                return true;

            case "/usuarios" or "/quem" or "/users" or "/who":
                s.Enviar(Protocolo.PrefixoServidor + DescreverUsuarios());
                return true;

            case "/nick" or "/nome":
                TrocarNome(s, resto);
                return true;

            case "/msg" or "/w" or "/privado":
                EnviarPrivado(s, resto);
                return true;

            case "/me":
                if (resto.Length == 0) { s.Enviar(Protocolo.PrefixoServidor + "Uso: /me <ação>"); return true; }
                string acao = $"* {s.Nome} {resto}";
                Log.Chat(acao);
                Divulgar(acao, s);
                return true;

            case "/sair" or "/quit" or "/exit":
                return false;

            default:
                s.Enviar($"{Protocolo.PrefixoServidor}Comando desconhecido: {cmd}. Digite /ajuda.");
                return true;
        }
    }

    private static readonly string[] AjudaComandos =
    [
        "Comandos disponíveis:",
        "  /usuarios              lista quem está online",
        "  /msg <nome> <texto>    mensagem privada",
        "  /nick <novo nome>      troca seu nome",
        "  /me <ação>             ex.: /me acenou",
        "  /sair                  sai da sala",
    ];

    public string DescreverUsuarios()
    {
        var nomes = _sessoes.Values.Where(x => x.Nome.Length > 0).Select(x => x.Nome).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return nomes.Count == 0 ? "Ninguém online." : $"Online ({nomes.Count}): {string.Join(", ", nomes)}";
    }

    private void TrocarNome(Sessao s, string novo)
    {
        novo = NormalizarNome(novo);
        if (ValidarNome(novo) is { } erro) { s.Enviar(Protocolo.PrefixoServidor + erro); return; }

        string antigo;
        lock (_travaNomes)
        {
            if (_sessoes.Values.Any(x => x != s && x.Nome.Equals(novo, StringComparison.OrdinalIgnoreCase)))
            {
                s.Enviar($"{Protocolo.PrefixoServidor}O nome \"{novo}\" já está em uso.");
                return;
            }
            antigo = s.Nome;
            s.Nome = novo;
        }

        s.Enviar(Protocolo.PrefixoSeuNome + novo);
        string aviso = $"{antigo} agora é {novo}.";
        Log.Info(aviso);
        Divulgar(aviso, s);
    }

    private void EnviarPrivado(Sessao s, string resto)
    {
        int espaco = resto.IndexOf(' ');
        if (espaco <= 0) { s.Enviar(Protocolo.PrefixoServidor + "Uso: /msg <nome> <texto>"); return; }

        string alvo = resto[..espaco];
        string texto = resto[(espaco + 1)..].Trim();
        var destino = BuscarPorNome(alvo);
        if (destino is null) { s.Enviar($"{Protocolo.PrefixoServidor}Usuário \"{alvo}\" não está online."); return; }

        destino.Enviar($"(privado) {s.Nome}: {texto}");
        s.Enviar($"(privado para {destino.Nome}) {texto}");
    }

    // ----------------------------------------------------------- uso pelo admin

    public Sessao? BuscarPorNome(string nome) =>
        _sessoes.Values.FirstOrDefault(x => x.Nome.Equals(nome, StringComparison.OrdinalIgnoreCase));

    public bool Expulsar(string nome, string? motivo)
    {
        var s = BuscarPorNome(nome);
        if (s is null) return false;
        s.Enviar(Protocolo.PrefixoServidor + "Você foi removido da sala" + (string.IsNullOrWhiteSpace(motivo) ? "." : $": {motivo}"));
        _ = s.EncerrarAposEnviarAsync(TimeSpan.FromSeconds(2));
        return true;
    }

    public void Anunciar(string texto) => Divulgar(Protocolo.PrefixoServidor + texto, null);

    public async Task EncerrarTodosAsync(string aviso)
    {
        Anunciar(aviso);
        await Task.WhenAll(_sessoes.Values.Select(s => s.EncerrarAposEnviarAsync(TimeSpan.FromSeconds(2))));
    }

    // ------------------------------------------------------------------ apoio

    private void Divulgar(string mensagem, Sessao? remetente)
    {
        foreach (var s in _sessoes.Values)
            if (s != remetente && s.Nome.Length > 0)
                s.Enviar(mensagem);
    }

    private static string? ValidarNome(string nome)
    {
        if (nome.Length == 0) return "Nome vazio.";
        if (nome.Length > Protocolo.TamanhoMaximoNome) return $"Nome muito longo (máximo {Protocolo.TamanhoMaximoNome} caracteres).";
        if (nome.StartsWith('/') || nome.StartsWith('[') || nome.StartsWith('*') || nome.StartsWith('('))
            return "O nome não pode começar com / [ * ou (.";
        if (nome.IndexOfAny([':', ',']) >= 0) return "O nome não pode ter \":\" nem \",\".";
        return null;
    }

    /// <summary>Espaços viram "_" para o nome funcionar em /msg (na v1 eram permitidos).</summary>
    private static string NormalizarNome(string nome) =>
        string.Join('_', LimparTexto(nome).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Se o nome já existe, acrescenta um número (Mr.Blue → Mr.Blue2).</summary>
    private string NomeDisponivel(string desejado)
    {
        bool EmUso(string n) => _sessoes.Values.Any(x => x.Nome.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (!EmUso(desejado)) return desejado;
        for (int i = 2; ; i++)
        {
            string sufixo = i.ToString();
            string candidato = desejado.Length + sufixo.Length > Protocolo.TamanhoMaximoNome
                ? desejado[..(Protocolo.TamanhoMaximoNome - sufixo.Length)] + sufixo
                : desejado + sufixo;
            if (!EmUso(candidato)) return candidato;
        }
    }

    /// <summary>Remove caracteres de controle (inclusive sequências ANSI que bagunçariam o terminal dos outros).</summary>
    private static string LimparTexto(string texto)
    {
        if (!texto.Any(char.IsControl)) return texto;
        var sb = new StringBuilder(texto.Length);
        foreach (char c in texto)
            if (!char.IsControl(c)) sb.Append(c);
        return sb.ToString();
    }
}

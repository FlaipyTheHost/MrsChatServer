namespace MrsChatServer;

sealed class OpcoesServidor
{
    public const string ArquivoConfiguracao = "MrsChatServer.conf";
    public const string RelayPadrao = "bore.pub";
    public const int PortaControleBore = 7835;

    public int Porta { get; set; } = 9000;
    public string? Senha { get; set; }
    public int MaxClientes { get; set; } = 50;

    /// <summary>Abre um túnel público (bore) para aceitar clientes de fora da rede local.</summary>
    public bool Publico { get; set; }
    public string RelayHost { get; set; } = RelayPadrao;
    public int RelayPorta { get; set; } = PortaControleBore;
    public string? RelaySegredo { get; set; }
    /// <summary>Porta pública desejada no relay (0 = o relay escolhe).</summary>
    public int PortaPublica { get; set; }

    public bool MostrarAjuda { get; set; }
    public bool MostrarVersao { get; set; }

    /// <summary>
    /// Ordem de prioridade: valores padrão &lt; arquivo .conf &lt; argumentos de linha de comando.
    /// </summary>
    public static OpcoesServidor Carregar(string[] args)
    {
        var op = new OpcoesServidor();
        op.AplicarArquivo();
        op.AplicarArgumentos(args);
        return op;
    }

    private void AplicarArquivo()
    {
        string? caminho = LocalizarArquivo();
        if (caminho is null) return;

        int numero = 0;
        foreach (var linhaBruta in File.ReadAllLines(caminho))
        {
            numero++;
            string linha = linhaBruta.Trim();
            if (linha.Length == 0 || linha.StartsWith('#')) continue;

            int igual = linha.IndexOf('=');
            if (igual <= 0)
                throw new ErroDeOpcao($"{caminho}:{numero}: linha inválida \"{linha}\" (esperado chave=valor).");

            string chave = linha[..igual].Trim().ToLowerInvariant();
            string valor = linha[(igual + 1)..].Trim();
            string origem = $"{caminho}:{numero}";

            switch (chave)
            {
                case "porta": Porta = LerPorta(valor, origem); break;
                case "senha": Senha = valor.Length > 0 ? valor : null; break;
                case "max_clientes": MaxClientes = LerInteiro(valor, origem, 1, 10_000); break;
                case "publico": Publico = LerBooleano(valor, origem); break;
                case "relay": DefinirRelay(valor, origem); break;
                case "relay_segredo": RelaySegredo = valor.Length > 0 ? valor : null; break;
                case "porta_publica": PortaPublica = LerInteiro(valor, origem, 0, 65535); break;
                default: Log.Aviso($"{origem}: chave desconhecida \"{chave}\" ignorada."); break;
            }
        }
    }

    private void AplicarArgumentos(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Valor()
            {
                if (i + 1 >= args.Length) throw new ErroDeOpcao($"A opção {a} precisa de um valor.");
                return args[++i];
            }

            switch (a)
            {
                case "-h" or "--ajuda" or "--help" or "/?": MostrarAjuda = true; break;
                case "-v" or "--versao" or "--version": MostrarVersao = true; break;
                case "-p" or "--porta": Porta = LerPorta(Valor(), a); break;
                case "--senha": Senha = Valor(); break;
                case "--max-clientes": MaxClientes = LerInteiro(Valor(), a, 1, 10_000); break;
                case "--publico" or "--public": Publico = true; break;
                case "--relay": DefinirRelay(Valor(), a); Publico = true; break;
                case "--relay-segredo": RelaySegredo = Valor(); break;
                case "--porta-publica": PortaPublica = LerInteiro(Valor(), a, 0, 65535); Publico = true; break;
                default:
                    // Compatibilidade com a v1: "MrsChatServer 9000"
                    if (!a.StartsWith('-') && int.TryParse(a, out _))
                        Porta = LerPorta(a, "argumento");
                    else
                        throw new ErroDeOpcao($"Opção desconhecida: {a}");
                    break;
            }
        }
    }

    private void DefinirRelay(string valor, string origem)
    {
        if (string.IsNullOrWhiteSpace(valor)) throw new ErroDeOpcao($"{origem}: relay vazio.");
        int doisPontos = valor.LastIndexOf(':');
        if (doisPontos > 0 && !valor.EndsWith(']'))
        {
            RelayHost = valor[..doisPontos];
            RelayPorta = LerPorta(valor[(doisPontos + 1)..], origem);
        }
        else
        {
            RelayHost = valor;
            RelayPorta = PortaControleBore;
        }
        RelayHost = RelayHost.Trim('[', ']');
    }

    private static string? LocalizarArquivo()
    {
        // Diretório atual (comportamento da v1) e, como alternativa, a pasta do executável.
        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            string c = Path.Combine(dir, ArquivoConfiguracao);
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private static int LerPorta(string v, string origem) => LerInteiro(v, origem, 1, 65535);

    private static int LerInteiro(string v, string origem, int min, int max)
    {
        if (!int.TryParse(v, out int n) || n < min || n > max)
            throw new ErroDeOpcao($"{origem}: valor inválido \"{v}\" (esperado número entre {min} e {max}).");
        return n;
    }

    private static bool LerBooleano(string v, string origem) => v.ToLowerInvariant() switch
    {
        "1" or "true" or "sim" or "s" or "yes" or "on" => true,
        "0" or "false" or "nao" or "não" or "n" or "no" or "off" or "" => false,
        _ => throw new ErroDeOpcao($"{origem}: valor inválido \"{v}\" (use sim/nao)."),
    };

    public static string TextoAjuda => $"""
        MrsChatServer v{Protocolo.Versao} - servidor de sala para clientes MrsChat

        Uso:
          MrsChatServer [opções]
          MrsChatServer <porta>                 (forma antiga, ainda aceita)

        Opções:
          -p, --porta <n>          Porta TCP local (padrão: 9000)
              --senha <texto>      Exige senha para entrar na sala
              --max-clientes <n>   Limite de usuários conectados (padrão: 50)

          Acesso pela internet (funciona atrás de NAT/CGNAT, sem abrir porta no roteador):
              --publico            Abre um túnel público via {RelayPadrao}
              --relay <host[:p]>   Usa outro relay bore (ex.: um VPS seu rodando "bore server")
              --relay-segredo <s>  Segredo do relay, se ele exigir (bore server --secret)
              --porta-publica <n>  Pede uma porta específica no relay (padrão: qualquer)

          -h, --ajuda              Mostra esta ajuda
          -v, --versao             Mostra a versão

        Arquivo {ArquivoConfiguracao} (opcional; argumentos têm prioridade):
          porta=9000
          senha=
          max_clientes=50
          publico=nao
          relay=bore.pub
          relay_segredo=
          porta_publica=0

        Exemplos:
          MrsChatServer --porta 9000
          MrsChatServer --publico --senha minhasenha
          MrsChatServer --relay meuvps.com --relay-segredo xyz
        """;
}

sealed class ErroDeOpcao(string mensagem) : Exception(mensagem);

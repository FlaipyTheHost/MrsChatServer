using System.Net.NetworkInformation;
using System.Net.Sockets;
using MrsChatServer;

OpcoesServidor op;
try
{
    op = OpcoesServidor.Carregar(args);
}
catch (ErroDeOpcao ex)
{
    Log.Erro(ex.Message);
    Console.WriteLine("Use --ajuda para ver as opções.");
    Pausar();
    return 2;
}

if (op.MostrarAjuda) { Console.WriteLine(OpcoesServidor.TextoAjuda); return 0; }
if (op.MostrarVersao) { Console.WriteLine($"MrsChatServer {Protocolo.Versao}"); return 0; }

Log.Destaque($"MrsChatServer v{Protocolo.Versao}");
Log.Destaque("--------------------------------------------");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var servidor = new ServidorChat(op);
Task tarefaServidor;
try
{
    tarefaServidor = servidor.ExecutarAsync(cts.Token);
    await Task.Delay(100); // deixa o listener subir (ou falhar) antes de continuar
    if (tarefaServidor.IsFaulted) await tarefaServidor;
}
catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
{
    Log.Erro($"A porta {op.Porta} já está em uso. Use outra com --porta.");
    Pausar();
    return 1;
}

if (op.Senha is not null) Log.Info("Sala protegida por senha.");
MostrarEnderecosLocais(op.Porta);

TunelBore? tunel = null;
Task tarefaTunel = Task.CompletedTask;
if (op.Publico)
{
    Log.Info($"Abrindo túnel público via {op.RelayHost}...");
    tunel = new TunelBore(op.RelayHost, op.RelayPorta, op.Porta, op.RelaySegredo, op.PortaPublica);
    tunel.EnderecoDisponivel += endereco =>
    {
        Log.Sucesso("Túnel público ativo!");
        Log.Destaque($"  Endereço para amigos fora da sua rede:  {endereco}");
        Log.Destaque($"  No cliente:  MrsChat --nome SeuNome --servidor {endereco}");
        if (op.Senha is null)
            Log.Aviso("A sala está aberta para qualquer um que tiver o endereço. Considere usar --senha.");
    };
    tarefaTunel = tunel.ExecutarAsync(cts.Token);
}
else
{
    Log.Info("Para aceitar amigos fora da sua rede (inclusive atrás de CGNAT), use --publico.");
}

Log.Info("Digite /ajuda para ver os comandos do console. Ctrl+C encerra.");

// Console de administração. Se não houver terminal (serviço, nohup), apenas espera.
_ = Task.Run(() => ConsoleAdmin(servidor, tunel, cts));

try
{
    await tarefaServidor;
}
catch (Exception ex) when (!cts.IsCancellationRequested)
{
    Log.Erro($"O servidor parou: {ex.Message}");
    return 1;
}

Log.Info("Encerrando...");
await servidor.EncerrarTodosAsync("O servidor está sendo encerrado.");
await Task.WhenAny(tarefaTunel, Task.Delay(1000));
return 0;

// ---------------------------------------------------------------------------

static void ConsoleAdmin(ServidorChat servidor, TunelBore? tunel, CancellationTokenSource cts)
{
    while (!cts.IsCancellationRequested)
    {
        string? linha;
        try { linha = Console.ReadLine(); }
        catch (IOException) { return; }
        if (linha is null) return; // sem entrada (ex.: rodando em segundo plano)

        linha = linha.Trim();
        if (linha.Length == 0) continue;

        if (!linha.StartsWith('/'))
        {
            servidor.Anunciar(linha);
            Log.Info($"Anúncio enviado: {linha}");
            continue;
        }

        int espaco = linha.IndexOf(' ');
        string cmd = (espaco < 0 ? linha : linha[..espaco]).ToLowerInvariant();
        string resto = espaco < 0 ? "" : linha[(espaco + 1)..].Trim();

        switch (cmd)
        {
            case "/ajuda" or "/help":
                Console.WriteLine("""
                    Comandos do console do servidor:
                      <texto>                     envia um anúncio para todos
                      /usuarios                   lista quem está online
                      /expulsar <nome> [motivo]   remove alguém da sala
                      /endereco                   mostra os endereços de acesso
                      /sair                       encerra o servidor
                    """);
                break;
            case "/usuarios" or "/quem":
                Log.Info(servidor.DescreverUsuarios());
                break;
            case "/expulsar" or "/kick":
                if (resto.Length == 0) { Log.Aviso("Uso: /expulsar <nome> [motivo]"); break; }
                int sp = resto.IndexOf(' ');
                string nome = sp < 0 ? resto : resto[..sp];
                string? motivo = sp < 0 ? null : resto[(sp + 1)..];
                if (servidor.Expulsar(nome, motivo)) Log.Info($"{nome} foi removido.");
                else Log.Aviso($"Usuário \"{nome}\" não encontrado.");
                break;
            case "/endereco" or "/ip":
                if (tunel?.EnderecoPublico is { } pub) Log.Destaque($"Público: {pub}");
                else if (tunel is not null) Log.Aviso("Túnel público ainda não está ativo.");
                else Log.Info("Túnel público desligado (use --publico).");
                break;
            case "/sair" or "/exit" or "/quit":
                cts.Cancel();
                return;
            default:
                Log.Aviso($"Comando desconhecido: {cmd}. Digite /ajuda.");
                break;
        }
    }
}

static void MostrarEnderecosLocais(int porta)
{
    var ips = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
        .Select(a => a.ToString())
        .Distinct()
        .ToList();

    Log.Info($"Nesta máquina:   localhost:{porta}");
    foreach (var ip in ips) Log.Info($"Na rede local:   {ip}:{porta}");
}

static void Pausar()
{
    // Mantém a janela aberta quando o .exe é aberto com duplo clique no Windows.
    if (OperatingSystem.IsWindows() && !Console.IsInputRedirected && Environment.GetCommandLineArgs().Length <= 1)
    {
        Console.WriteLine("Pressione qualquer tecla para sair...");
        Console.ReadKey(true);
    }
}

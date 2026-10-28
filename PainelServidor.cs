using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MrsChatServer;

/// <summary>
/// Painel de "central de comunicação" em tela cheia, no mesmo visual de fósforo verde do cliente:
///
///   ■ cabeçalho: servidor, canal, porta, endereço público, status, relógio
///   ╔═[ RECURSOS ]═══════════════════╦═[ OPERADORES CONECTADOS ]══════════╗
///   ║ CPU/RAM do app e do sistema,   ║ indicativo, origem, tempo, msgs,   ║
///   ║ tráfego, chave do servidor     ║ cifrado ou não                      ║
///   ╠═[ TRÁFEGO INTERCEPTADO ]═══════╩══════════╦═[ REGISTRO ]═══════════╣
///   ║ só os bytes cifrados de cada mensagem      ║ conexões, avisos...    ║
///   ╠═[ CONSOLE DO OPERADOR ]════════════════════╩════════════════════════╣
///   ║ ADMIN ▸ comando                                                      ║
///   ╚═[ dicas ]════════════════════════════════════════════════════════════╝
///
/// O texto das mensagens nunca aparece aqui: o servidor decifra para repassar, mas a tela
/// mostra só o que passou pela rede (base64 → hexadecimal).
/// </summary>
sealed class PainelServidor : IDisposable
{
    public const int LarguraMinima = 80;
    public const int AlturaMinima = 24;
    private const int Padrao = -1;

    private const int Forte = (int)ConsoleColor.Green;
    private const int Fraca = (int)ConsoleColor.DarkGreen;
    private const int Alerta = (int)ConsoleColor.Yellow;
    private const int Perigo = (int)ConsoleColor.Red;
    private const int Branco = (int)ConsoleColor.White;
    private const int Preto = (int)ConsoleColor.Black;

    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    private readonly record struct Celula(string? T, int Fg, int Bg, bool Negrito)
    {
        public static readonly Celula Vazia = new(" ", Fraca, Preto, false);
    }

    private readonly ServidorChat _servidor;
    private readonly OpcoesServidor _op;
    private readonly IdentidadeServidor _identidade;
    private readonly Func<string?> _enderecoPublico;
    private readonly Func<string, bool> _executarComando;
    private readonly CancellationTokenSource _cts;

    private readonly object _trava = new();
    private readonly StringBuilder _entrada = new();
    private readonly List<string> _historico = [];
    private int _posHistorico;
    private Celula[,]? _anterior;
    private int _w, _h;
    private bool _redesenharTudo = true;
    private volatile bool _ativo = true;
    private readonly Thread _desenho, _teclado;

    public static bool Disponivel() =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected &&
        Environment.GetEnvironmentVariable("TERM") != "dumb" && Vt.Habilitar();

    public PainelServidor(ServidorChat servidor, OpcoesServidor op, IdentidadeServidor identidade,
                          Func<string?> enderecoPublico, Func<string, bool> executarComando, CancellationTokenSource cts)
    {
        _servidor = servidor;
        _op = op;
        _identidade = identidade;
        _enderecoPublico = enderecoPublico;
        _executarComando = executarComando;
        _cts = cts;

        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Out.Write("\x1b[?1049h\x1b[?7l\x1b[?25l\x1b[1 q\x1b[2J");
        Console.Out.Flush();
        Log.NoConsole = false;

        _desenho = new Thread(LacoDeDesenho) { IsBackground = true, Name = "Painel" };
        _teclado = new Thread(LacoDeTeclado) { IsBackground = true, Name = "PainelTeclado" };
        _desenho.Start();
        _teclado.Start();
    }

    public void Dispose()
    {
        if (!_ativo) return;
        _ativo = false;
        _desenho.Join(500);
        lock (_trava)
        {
            Console.Out.Write("\x1b[0m\x1b[0 q\x1b[?25h\x1b[?7h\x1b[?1049l");
            Console.Out.Flush();
        }
        Log.NoConsole = true;
    }

    // =============================================================== teclado

    private void LacoDeTeclado()
    {
        while (_ativo)
        {
            bool tem;
            try { tem = Console.KeyAvailable; } catch (InvalidOperationException) { return; }
            if (!tem) { Thread.Sleep(20); continue; }

            var k = Console.ReadKey(intercept: true);
            string? enviar = null;
            lock (_trava)
            {
                switch (k.Key)
                {
                    case ConsoleKey.Enter:
                        enviar = _entrada.ToString().Trim();
                        if (enviar.Length > 0 && (_historico.Count == 0 || _historico[^1] != enviar)) _historico.Add(enviar);
                        _posHistorico = _historico.Count;
                        _entrada.Clear();
                        break;
                    case ConsoleKey.Backspace:
                        if (_entrada.Length > 0) _entrada.Length--;
                        break;
                    case ConsoleKey.Escape:
                        _entrada.Clear();
                        break;
                    case ConsoleKey.UpArrow:
                        if (_posHistorico > 0) _entrada.Clear().Append(_historico[--_posHistorico]);
                        break;
                    case ConsoleKey.DownArrow:
                        if (_posHistorico < _historico.Count - 1) _entrada.Clear().Append(_historico[++_posHistorico]);
                        else { _posHistorico = _historico.Count; _entrada.Clear(); }
                        break;
                    case ConsoleKey.L when k.Modifiers.HasFlag(ConsoleModifiers.Control):
                        _redesenharTudo = true;
                        break;
                    default:
                        if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar) && _entrada.Length < 400) _entrada.Append(k.KeyChar);
                        break;
                }
            }
            if (!string.IsNullOrEmpty(enviar) && !_executarComando(enviar)) { _cts.Cancel(); return; }
        }
    }

    // =============================================================== desenho

    private void LacoDeDesenho()
    {
        while (_ativo)
        {
            try
            {
                lock (_trava)
                {
                    if (!_ativo) break;
                    int w = Math.Max(1, Console.WindowWidth), h = Math.Max(1, Console.WindowHeight);
                    if (w != _w || h != _h) { _w = w; _h = h; _redesenharTudo = true; }
                    Desenhar();
                }
            }
            catch (IOException) { }
            Thread.Sleep(200);
        }
    }

    private void Desenhar()
    {
        int W = _w, H = _h;
        var g = new Celula[H, W];
        for (int r = 0; r < H; r++) for (int c = 0; c < W; c++) g[r, c] = Celula.Vazia;
        (int, int)? cursor = null;

        if (W < LarguraMinima || H < AlturaMinima)
        {
            string m = $"TERMINAL PEQUENO DEMAIS · MÍNIMO {LarguraMinima}x{AlturaMinima}";
            Texto(g, H / 2, Math.Max(0, (W - Largura(m)) / 2), W, Cortar(m, W), Forte, negrito: true);
            Emitir(g, null);
            return;
        }

        var a = Telemetria.Atual;
        var sessoes = _servidor.Sessoes;

        // ---- medidas
        const int linhasRecursos = 11;
        int yTopo = 1, yMeio = yTopo + 1 + linhasRecursos, yConsole = H - 3;
        int xA = Math.Clamp(W * 52 / 100, 50, W - 30);     // divisão de cima
        int xB = Math.Clamp(W * 62 / 100, 40, W - 26);     // divisão do meio

        Cabecalho(g, W);

        // ---- molduras
        Linha(g, yTopo, W, '╔', '╗');
        Linha(g, yMeio, W, '╠', '╣');
        Linha(g, yConsole, W, '╠', '╣');
        Linha(g, H - 1, W, '╚', '╝');
        for (int r = yTopo + 1; r < H - 1; r++) if (r != yMeio && r != yConsole) { Borda(g, r, 0, '║'); Borda(g, r, W - 1, '║'); }
        for (int r = yTopo + 1; r < yMeio; r++) Borda(g, r, xA, '║');
        for (int r = yMeio + 1; r < yConsole; r++) Borda(g, r, xB, '║');
        Borda(g, yTopo, xA, '╦');
        if (xA == xB) Borda(g, yMeio, xA, '╬');
        else { Borda(g, yMeio, xA, '╩'); Borda(g, yMeio, xB, '╦'); }
        Borda(g, yConsole, xB, '╩');

        Titulo(g, yTopo, 2, xA - 1, "RECURSOS DO SISTEMA");
        Titulo(g, yTopo, xA + 2, W - 2, $"OPERADORES CONECTADOS: {sessoes.Count:00}");
        Titulo(g, yMeio, 2, Math.Min(xA, xB) - 1, "TRÁFEGO INTERCEPTADO · CIFRADO");
        Titulo(g, yMeio, xB + 2, W - 2, "REGISTRO DE EVENTOS");
        Titulo(g, yConsole, 2, xB - 1, "CONSOLE DO OPERADOR");
        Texto(g, H - 1, 2, W - 2, Cortar("[ TEXTO = ANÚNCIO · /USUARIOS · /EXPULSAR <NOME> · /ENDERECO · /SAIR · CTRL+C ENCERRA ]", W - 4), Fraca);

        Recursos(g, a, sessoes, yTopo + 1, 2, xA - 1);
        Operadores(g, sessoes, yTopo + 1, linhasRecursos, xA + 2, W - 2);
        TrafegoCifrado(g, yMeio + 1, yConsole - yMeio - 1, 2, xB - 1);
        Registro(g, yMeio + 1, yConsole - yMeio - 1, xB + 2, W - 2);

        // ---- console
        int c0 = Texto(g, H - 2, 2, W - 2, "ADMIN ▸ ", Fraca);
        string entrada = _entrada.ToString();
        int espaco = W - 3 - c0;
        if (Largura(entrada) >= espaco) entrada = "…" + entrada[^(espaco - 2)..];
        int fim = Texto(g, H - 2, c0, W - 2, entrada, Forte);
        cursor = (H - 2, fim);

        Emitir(g, cursor);
    }

    private void Cabecalho(Celula[,] g, int W)
    {
        for (int c = 0; c < W; c++) g[0, c] = new Celula(" ", Preto, Forte, false);
        var agora = DateTime.Now;
        string pub = _enderecoPublico() is { } p ? $"  │  PÚBLICO: {p.ToUpperInvariant()}" : "";
        string esquerda = $"■ MRS-NET · CENTRAL v{Protocolo.Versao}  │  CANAL: {_op.Sala.ToUpperInvariant()}  │  PORTA {_op.Porta}{pub}";
        string direita = $"{(agora.Second % 2 == 0 ? "◉" : "○")} NO AR  │  UPTIME {Duracao(agora - Telemetria.Inicio)}  │  {agora:HH:mm:ss} ";
        int xd = W - Largura(direita);
        Texto(g, 0, 1, xd - 1, Cortar(esquerda, xd - 2), Preto, Forte, negrito: true);
        Texto(g, 0, xd, W, direita, Preto, Forte, negrito: true);
    }

    private void Recursos(Celula[,] g, Telemetria.Amostra a, List<Sessao> sessoes, int y, int x0, int x1)
    {
        int larg = x1 - x0;
        int xv = x0 + 11;                 // coluna dos valores
        int barra = Math.Clamp(larg / 4, 8, 16);

        void Rotulo(int r, string t) => Texto(g, y + r, x0, xv, t, Fraca);

        Rotulo(0, "CPU APP");
        int c = Barra(g, y, xv, barra, a.CpuApp / 100, Forte);
        c = Texto(g, y, c + 1, x1, $"{a.CpuApp.ToString("0.0", Br),5}%", Forte, negrito: true);
        Sparkline(g, y, c + 2, x1, Telemetria.HistoricoCpu, Fraca);

        Rotulo(1, "CPU SIST");
        if (a.CpuSistema is { } cs)
        {
            c = Barra(g, y + 1, xv, barra, cs / 100, cs > 85 ? Alerta : Forte);
            Texto(g, y + 1, c + 1, x1, $"{cs.ToString("0.0", Br),5}%  · {Environment.ProcessorCount} NÚCLEOS", Forte);
        }
        else Texto(g, y + 1, xv, x1, $"N/D  · {Environment.ProcessorCount} NÚCLEOS", Fraca);

        Rotulo(2, "RAM APP");
        Texto(g, y + 2, xv, x1, Cortar($"{Bytes(a.RamApp)}  · HEAP GC {Bytes(a.HeapGc)}  · {a.Threads} THREADS", x1 - xv), Forte);

        Rotulo(3, "RAM SIST");
        if (a.RamSistemaTotal is { } total && a.RamSistemaUsada is { } usada)
        {
            double f = (double)usada / total;
            c = Barra(g, y + 3, xv, barra, f, f > 0.9 ? Alerta : Forte);
            Texto(g, y + 3, c + 1, x1, Cortar($"{Bytes(usada)} / {Bytes(total)}", x1 - c - 1), Forte);
        }
        else Texto(g, y + 3, xv, x1, a.RamSistemaTotal is { } t ? $"TOTAL {Bytes(t)}" : "N/D", Fraca);

        Rotulo(5, "TRÁFEGO");
        Texto(g, y + 5, xv, x1, Cortar($"▼ RX {Bytes(Telemetria.BytesRecebidos)}  ▲ TX {Bytes(Telemetria.BytesEnviados)}", x1 - xv), Forte);
        Rotulo(6, "TAXA");
        c = Texto(g, y + 6, xv, x1, $"{Bytes((long)(a.RxPorSeg + a.TxPorSeg))}/s".PadRight(11), Forte, negrito: true);
        Sparkline(g, y + 6, c + 1, x1, Telemetria.HistoricoTrafego, Forte);
        Rotulo(7, "MENSAGENS");
        Texto(g, y + 7, xv, x1, $"{Telemetria.Mensagens:N0}".Replace(',', '.') + $"  · {sessoes.Count}/{_op.MaxClientes} OPERADORES", Forte);

        Rotulo(8, "CHAVE");
        string[] grupos = _identidade.Impressao.Split(' ');
        Texto(g, y + 8, xv, x1, string.Join(' ', grupos[..4]), Branco, negrito: true);
        Texto(g, y + 9, xv, x1, string.Join(' ', grupos[4..]), Branco, negrito: true);
        int cifradas = sessoes.Count(s => s.Cifrada);
        Rotulo(10, "CIFRA");
        int cor = cifradas == sessoes.Count ? Forte : Alerta;
        Texto(g, y + 10, xv, x1, Cortar($"{CanalSeguro.Algoritmos} · {cifradas}/{sessoes.Count} CIFRADAS", x1 - xv), cor);
    }

    private void Operadores(Celula[,] g, List<Sessao> sessoes, int y, int altura, int x0, int x1)
    {
        int larg = x1 - x0;
        int cNome = x0, cOrigem = x0 + 14, cTempo = x1 - 21, cMsg = x1 - 12, cCanal = x1 - 6;
        bool comOrigem = cTempo - cOrigem >= 10;
        Texto(g, y, cNome, x1, "INDICATIVO", Fraca);
        if (comOrigem) Texto(g, y, cOrigem, cTempo, "ORIGEM", Fraca);
        Texto(g, y, cTempo, x1, "TEMPO", Fraca);
        Texto(g, y, cMsg, x1, "MSGS", Fraca);
        Texto(g, y, cCanal, x1, "CANAL", Fraca);

        if (sessoes.Count == 0) Texto(g, y + 2, x0, x1, "NENHUM OPERADOR NA REDE", Fraca);
        int cabem = altura - 1;
        for (int i = 0; i < Math.Min(cabem, sessoes.Count); i++)
        {
            var s = sessoes[i];
            int r = y + 1 + i;
            if (i == cabem - 1 && sessoes.Count > cabem) { Texto(g, r, x0, x1, $"… +{sessoes.Count - cabem + 1}", Fraca); break; }
            Texto(g, r, cNome, (comOrigem ? cOrigem : cTempo) - 1, Cortar($"▸ {s.Nome.ToUpperInvariant()}", (comOrigem ? cOrigem : cTempo) - 1 - cNome), Forte, negrito: true);
            if (comOrigem) Texto(g, r, cOrigem, cTempo - 1, Cortar(s.Endereco, cTempo - 1 - cOrigem), Fraca);
            Texto(g, r, cTempo, cMsg - 1, Duracao(DateTime.Now - s.ConectadoEm), Forte);
            Texto(g, r, cMsg, cCanal - 1, s.MensagensEnviadas.ToString().PadLeft(4), Forte);
            if (s.Cifrada) Texto(g, r, cCanal, x1, "◈ AES", Forte);
            else Texto(g, r, cCanal, x1, "⚠ PURO", Alerta, negrito: true);
        }
    }

    private void TrafegoCifrado(Celula[,] g, int y, int altura, int x0, int x1)
    {
        var itens = Telemetria.TrafegoRecente(altura);
        if (itens.Count == 0) { Texto(g, y, x0, x1, "AGUARDANDO TRÁFEGO...", Fraca); return; }
        int r = y + altura - itens.Count;
        foreach (var t in itens)
        {
            int c = Texto(g, r, x0, x1, $"[{t.Hora:HH:mm:ss}] ", Fraca);
            // Colunas fixas: autor, destino, tamanho e os bytes cifrados alinhados.
            c = Texto(g, r, c, x1, Cortar(t.Autor.ToUpperInvariant(), 10).PadRight(10), Forte, negrito: true);
            c = Texto(g, r, c, x1, " ▸ ", Fraca);
            c = Texto(g, r, c, x1, Cortar(t.Destino.ToUpperInvariant(), 15).PadRight(15), Forte);
            c = Texto(g, r, c, x1, $" {t.Bytes,4} B ", Fraca);
            if (t.Cifrado)
            {
                var hex = new StringBuilder();
                foreach (byte b in t.Amostra) hex.Append(b.ToString("X2"));
                string agrupado = string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.ToString(i * 4, 4)));
                Texto(g, r, c, x1, Cortar(agrupado, x1 - c), Forte);
            }
            else
            {
                Texto(g, r, c, x1, Cortar("⚠ SEM CRIPTO · ▒▒▒▒▒▒▒▒▒▒▒▒", x1 - c), Alerta);
            }
            r++;
        }
    }

    private void Registro(Celula[,] g, int y, int altura, int x0, int x1)
    {
        // Quebra as linhas do registro para caber na coluna; mostra as mais recentes embaixo.
        int larg = x1 - x0;
        var linhas = new List<(string texto, int cor)>();
        foreach (var e in Telemetria.EventosRecentes(altura * 2))
        {
            string t = $"{e.Hora:HH:mm:ss} {e.Texto}";
            int cor = e.Cor switch
            {
                ConsoleColor.Yellow => Alerta, ConsoleColor.Red => Perigo,
                ConsoleColor.Cyan => Branco, ConsoleColor.Green => Forte, _ => Fraca,
            };
            while (Largura(t) > larg)
            {
                int corte = Math.Min(t.Length, larg);
                int esp = t.LastIndexOf(' ', corte - 1, Math.Min(corte, larg / 2));
                if (esp <= 9) esp = corte;
                linhas.Add((t[..esp], cor));
                t = "         " + t[esp..].TrimStart();
            }
            linhas.Add((t, cor));
        }
        var ultimas = linhas.TakeLast(altura).ToList();
        for (int i = 0; i < ultimas.Count; i++) Texto(g, y + altura - ultimas.Count + i, x0, x1, ultimas[i].texto, ultimas[i].cor);
    }

    // ---------------------------------------------------------- componentes

    private static int Barra(Celula[,] g, int r, int c, int largura, double fracao, int cor)
    {
        int cheio = (int)Math.Round(Math.Clamp(fracao, 0, 1) * largura);
        Texto(g, r, c, c + 1, "▕", Fraca);
        for (int i = 0; i < largura; i++) g[r, c + 1 + i] = new Celula(i < cheio ? "█" : "░", i < cheio ? cor : Fraca, Preto, false);
        Texto(g, r, c + 1 + largura, c + 2 + largura, "▏", Fraca);
        return c + 2 + largura;
    }

    private static void Sparkline(Celula[,] g, int r, int c0, int c1, double[] valores, int cor)
    {
        const string niveis = " ▁▂▃▄▅▆▇█";
        int n = c1 - c0;
        if (n < 4 || valores.Length == 0) return;
        var v = valores.TakeLast(n).ToArray();
        double max = Math.Max(v.Max(), 1e-9);
        int inicio = c1 - v.Length;
        for (int i = 0; i < v.Length; i++)
        {
            int nivel = v[i] <= 0 ? 0 : Math.Max(1, (int)Math.Round(v[i] / max * 8));
            g[r, inicio + i] = new Celula(niveis[nivel].ToString(), cor, Preto, false);
        }
    }

    private static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => (b / 1024.0).ToString("0.0", Br) + " KB",
        < 1024L * 1024 * 1024 => (b / 1048576.0).ToString("0.0", Br) + " MB",
        _ => (b / 1073741824.0).ToString("0.0", Br) + " GB",
    };

    private static string Duracao(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    // ---------------------------------------------------------- grade/ANSI

    private static void Linha(Celula[,] g, int r, int W, char esq, char dir)
    {
        Borda(g, r, 0, esq);
        for (int c = 1; c < W - 1; c++) Borda(g, r, c, '═');
        Borda(g, r, W - 1, dir);
    }

    private static void Borda(Celula[,] g, int r, int c, char ch) => g[r, c] = new Celula(ch.ToString(), Fraca, Preto, false);

    private static void Titulo(Celula[,] g, int r, int x0, int x1, string t)
    {
        string s = $"[ {t} ]";
        if (Largura(s) > x1 - x0) s = $"[ {Cortar(t, Math.Max(1, x1 - x0 - 4))} ]";
        Texto(g, r, x0, x1, s, Forte, negrito: true);
    }

    private static int Texto(Celula[,] g, int r, int c, int cMax, string s, int fg, int bg = Preto, bool negrito = false)
    {
        if (r < 0 || r >= g.GetLength(0)) return c;
        foreach (Rune rn in s.EnumerateRunes())
        {
            if (Rune.IsControl(rn)) continue;
            int l = LarguraRune(rn);
            if (l == 0) continue;
            if (c + l > cMax || c < 0) break;
            g[r, c] = new Celula(rn.ToString(), fg, bg, negrito);
            if (l == 2) g[r, c + 1] = new Celula("", fg, bg, negrito);
            c += l;
        }
        return c;
    }

    private static int Largura(string s) => s.EnumerateRunes().Sum(LarguraRune);

    private static string Cortar(string s, int largura)
    {
        if (largura <= 0) return "";
        if (Largura(s) <= largura) return s;
        var sb = new StringBuilder();
        int usado = 0;
        foreach (Rune r in s.EnumerateRunes())
        {
            int l = LarguraRune(r);
            if (usado + l > largura - 1) break;
            sb.Append(r.ToString());
            usado += l;
        }
        return sb.Append('…').ToString();
    }

    private static int LarguraRune(Rune r)
    {
        var cat = Rune.GetUnicodeCategory(r);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        int v = r.Value;
        bool largo = (v >= 0x1100 && v <= 0x115F) || (v >= 0x2E80 && v <= 0xA4CF) || (v >= 0xAC00 && v <= 0xD7A3) ||
                     (v >= 0xF900 && v <= 0xFAFF) || (v >= 0xFF00 && v <= 0xFF60) || (v >= 0x1F300 && v <= 0x1FAFF) ||
                     (v >= 0x20000 && v <= 0x3FFFD);
        return largo ? 2 : 1;
    }

    private void Emitir(Celula[,] g, (int linha, int coluna)? cursor)
    {
        int H = g.GetLength(0), W = g.GetLength(1);
        var sb = new StringBuilder(W * H * 2);
        sb.Append("\x1b[?25l");
        if (_redesenharTudo || _anterior is null || _anterior.GetLength(0) != H || _anterior.GetLength(1) != W)
        {
            sb.Append("\x1b[0;40m\x1b[2J");
            _anterior = null;
        }
        for (int r = 0; r < H; r++)
        {
            if (_anterior is not null && Enumerable.Range(0, W).All(c => g[r, c] == _anterior[r, c])) continue;
            sb.Append("\x1b[").Append(r + 1).Append(";1H");
            int fg = int.MinValue, bg = int.MinValue; bool neg = false;
            for (int c = 0; c < W; c++)
            {
                var cel = g[r, c];
                if (cel.T == "") continue;
                if (cel.Fg != fg || cel.Bg != bg || cel.Negrito != neg)
                {
                    fg = cel.Fg; bg = cel.Bg; neg = cel.Negrito;
                    sb.Append("\x1b[0").Append(neg ? ";1" : "").Append(';').Append(Codigo(fg, false)).Append(';').Append(Codigo(bg, true)).Append('m');
                }
                sb.Append(cel.T ?? " ");
            }
            sb.Append("\x1b[0m");
        }
        if (cursor is { } cur)
            sb.Append("\x1b[0;92;40m\x1b[").Append(cur.linha + 1).Append(';').Append(cur.coluna + 1).Append("H\x1b[?25h");
        Console.Out.Write(sb.ToString());
        Console.Out.Flush();
        _anterior = g;
        _redesenharTudo = false;
    }

    private static string Codigo(int cor, bool fundo)
    {
        if (cor == Padrao) return fundo ? "49" : "39";
        int b = (ConsoleColor)cor switch
        {
            ConsoleColor.Black => 30, ConsoleColor.DarkRed => 31, ConsoleColor.DarkGreen => 32,
            ConsoleColor.DarkYellow => 33, ConsoleColor.DarkBlue => 34, ConsoleColor.DarkMagenta => 35,
            ConsoleColor.DarkCyan => 36, ConsoleColor.Gray => 37, ConsoleColor.DarkGray => 90,
            ConsoleColor.Red => 91, ConsoleColor.Green => 92, ConsoleColor.Yellow => 93,
            ConsoleColor.Blue => 94, ConsoleColor.Magenta => 95, ConsoleColor.Cyan => 96,
            _ => 97,
        };
        return (b + (fundo ? 10 : 0)).ToString();
    }

    /// <summary>No Windows, liga o processamento de sequências ANSI no console (Windows 10+).</summary>
    private static class Vt
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int n);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr h, out uint modo);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr h, uint modo);

        public static bool Habilitar()
        {
            if (!OperatingSystem.IsWindows()) return true;
            try
            {
                IntPtr h = GetStdHandle(-11);
                return GetConsoleMode(h, out uint m) && ((m & 4) != 0 || SetConsoleMode(h, m | 4));
            }
            catch (Exception) { return false; }
        }
    }
}

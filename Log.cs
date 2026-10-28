namespace MrsChatServer;

/// <summary>
/// Registro de eventos do servidor. Sempre vai para a <see cref="Telemetria"/> (painel);
/// no modo simples também é escrito no console, com horário e cor.
/// </summary>
static class Log
{
    private static readonly object Trava = new();

    /// <summary>false quando o painel ocupa a tela (aí escrever no console bagunçaria o desenho).</summary>
    public static bool NoConsole { get; set; } = true;

    public static void Info(string msg) => Escrever(msg, null);
    public static void Sucesso(string msg) => Escrever(msg, ConsoleColor.Green);
    public static void Aviso(string msg) => Escrever(msg, ConsoleColor.Yellow);
    public static void Erro(string msg) => Escrever(msg, ConsoleColor.Red);
    /// <summary>Passagem de mensagem: só metadados e bytes cifrados, nunca o texto.</summary>
    public static void Trafego(string msg) => Escrever(msg, ConsoleColor.DarkGreen, registrar: false);

    public static void Destaque(string msg)
    {
        Telemetria.RegistrarEvento(msg.Trim(), ConsoleColor.Cyan);
        if (!NoConsole) return;
        lock (Trava)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(msg);
            Console.ResetColor();
        }
    }

    private static void Escrever(string msg, ConsoleColor? cor, bool registrar = true)
    {
        if (registrar) Telemetria.RegistrarEvento(msg, cor);
        if (!NoConsole) return;
        lock (Trava)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            if (cor is { } c) Console.ForegroundColor = c; else Console.ResetColor();
            Console.WriteLine(msg);
            Console.ResetColor();
        }
    }
}

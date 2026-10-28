namespace MrsChatServer;

/// <summary>Saída do console do servidor, com horário e cor.</summary>
static class Log
{
    private static readonly object Trava = new();

    public static void Info(string msg) => Escrever(msg, null);
    public static void Sucesso(string msg) => Escrever(msg, ConsoleColor.Green);
    public static void Aviso(string msg) => Escrever(msg, ConsoleColor.Yellow);
    public static void Erro(string msg) => Escrever(msg, ConsoleColor.Red);
    public static void Chat(string msg) => Escrever(msg, ConsoleColor.Gray);

    public static void Destaque(string msg)
    {
        lock (Trava)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(msg);
            Console.ResetColor();
        }
    }

    private static void Escrever(string msg, ConsoleColor? cor)
    {
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

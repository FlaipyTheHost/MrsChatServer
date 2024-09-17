using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class Program
{
    private static List<TcpClient> clientes = new List<TcpClient>();
    private static Dictionary<TcpClient, string> nomesClientes = new Dictionary<TcpClient, string>();
    private static int porta;

    static async Task Main(string[] args)
    {

        porta = 9000;

        if (args.Length > 0 && int.TryParse(args[0], out int argPorta)) //Argumento
        {
            porta = argPorta;
        }
        else //Arquivo
        {
            if (!LerArquivoConfiguracao(out porta))
            {
                Console.WriteLine("Não foi possível ler o arquivo de configuração e a porta não foi fornecida como argumento.");
                Console.ReadKey();
                return;
            }
        }

        TcpListener listener = new TcpListener(IPAddress.Any, porta);
        listener.Start();
        Console.WriteLine($"Servidor aguardando clientes na porta {porta}...");

        while (true)
        {
            TcpClient cliente = await listener.AcceptTcpClientAsync();
            clientes.Add(cliente);

            NetworkStream stream = cliente.GetStream();
            byte[] buffer = new byte[1024];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            string nomeCliente = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();

            nomesClientes[cliente] = nomeCliente;
            Console.WriteLine($"Cliente conectado: {nomeCliente}");

            string mensagemEntrada = $"{nomeCliente} conectou-se.";
            DivulgarMensagem(mensagemEntrada, cliente);

            _ = Task.Run(() => TratarCliente(cliente));
        }
    }

    private static bool LerArquivoConfiguracao(out int porta)
    {
        porta = 0;
        try
        {
            var linhasConfiguracao = File.ReadAllLines("MrsChatServer.conf");
            foreach (var linha in linhasConfiguracao)
            {
                if (linha.StartsWith("porta="))
                {
                    porta = int.Parse(linha.Substring(6));
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao ler o arquivo de configuração: {ex.Message}");
            Console.ReadKey();
        }
        return false;
    }

    private static async Task TratarCliente(TcpClient cliente)
    {
        NetworkStream stream = cliente.GetStream();
        byte[] buffer = new byte[1024];
        string mensagem;

        string nomeCliente = nomesClientes[cliente];
        try
        {
            while (true)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;  // Cliente desconectado

                mensagem = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                if (mensagem.ToLower() == "exit") break;

                string mensagemFormatada = $"{nomeCliente}: {mensagem}";
                Console.WriteLine(mensagemFormatada);

                DivulgarMensagem(mensagemFormatada, cliente);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro: {ex.Message}");
        }
        finally
        {
            clientes.Remove(cliente);
            nomesClientes.Remove(cliente);
            string mensagemSaida = $"{nomeCliente} desconectou.";
            DivulgarMensagem(mensagemSaida, cliente);

            cliente.Close();
        }
    }

    private static async void DivulgarMensagem(string mensagem, TcpClient clienteRemetente)
    {
        foreach (var cliente in clientes)
        {
            if (cliente != clienteRemetente)
            {
                try
                {
                    NetworkStream outroStream = cliente.GetStream();
                    byte[] msg = Encoding.UTF8.GetBytes(mensagem + "\n");
                    await outroStream.WriteAsync(msg, 0, msg.Length);
                }
                catch
                {
                    
                }
            }
        }
    }
}

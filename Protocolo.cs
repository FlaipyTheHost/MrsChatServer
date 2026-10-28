namespace MrsChatServer;

/// <summary>
/// Textos fixos do protocolo. O protocolo continua sendo texto UTF-8, uma mensagem por linha,
/// para manter compatibilidade com o cliente MrsChat v1.0.
/// </summary>
static class Protocolo
{
    public const string Versao = "2.1";

    /// <summary>Prefixo das mensagens geradas pelo servidor.</summary>
    public const string PrefixoServidor = "[servidor] ";

    /// <summary>
    /// Enviada quando a sala exige senha. O cliente v2 responde sozinho se recebeu --senha;
    /// no cliente v1 o usuário simplesmente digita a senha como se fosse uma mensagem.
    /// </summary>
    public const string PedidoSenha = PrefixoServidor + "Esta sala exige senha. Digite a senha:";

    /// <summary>Informa ao cliente o nome efetivo dele (pode mudar se o nome já estava em uso, ou via /nick).</summary>
    public const string PrefixoSeuNome = PrefixoServidor + "Seu nome nesta sala é: ";

    /// <summary>Nome da sala, mostrado no cabeçalho do cliente v2.1.</summary>
    public const string PrefixoSala = PrefixoServidor + "Sala: ";

    /// <summary>Resposta de /usuarios; o cliente v2.1 usa para preencher o painel de integrantes.</summary>
    public const string PrefixoOnline = PrefixoServidor + "Online (";

    public const int TamanhoMaximoLinha = 4096;
    public const int TamanhoMaximoNome = 24;
}

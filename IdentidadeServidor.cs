using System.Security.Cryptography;

namespace MrsChatServer;

/// <summary>
/// Chave de identidade do servidor (ECDSA P-256), guardada num arquivo PEM.
/// É criada na primeira execução e reaproveitada depois, para a impressão digital
/// continuar a mesma e os clientes poderem reconhecer o servidor.
/// </summary>
sealed class IdentidadeServidor : IDisposable
{
    public const string ArquivoPadrao = "MrsChatServer.chave";

    public ECDsa Chave { get; }
    public string Caminho { get; }
    public bool Nova { get; }
    public byte[] ChavePublica { get; }
    public string Impressao => CanalSeguro.Impressao(ChavePublica);
    public string ImpressaoCompleta => CanalSeguro.Impressao(ChavePublica, 16);

    private IdentidadeServidor(ECDsa chave, string caminho, bool nova)
    {
        Chave = chave;
        Caminho = caminho;
        Nova = nova;
        ChavePublica = chave.ExportSubjectPublicKeyInfo();
    }

    public static IdentidadeServidor Carregar(string? caminhoInformado)
    {
        string caminho = caminhoInformado ?? Localizar();
        var chave = ECDsa.Create();
        if (File.Exists(caminho))
        {
            try
            {
                chave.ImportFromPem(File.ReadAllText(caminho));
                if (chave.KeySize != 256) throw new CryptographicException("a chave não é P-256");
                return new IdentidadeServidor(chave, caminho, nova: false);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                chave.Dispose();
                throw new ErroDeOpcao($"Não foi possível ler a chave do servidor em {caminho}: {ex.Message}. " +
                                      "Apague o arquivo para gerar outra (os clientes vão estranhar a mudança).");
            }
        }

        chave.Dispose();
        var nova = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = nova.ExportPkcs8PrivateKeyPem();
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(caminho, pem);
        }
        else
        {
            // Só o dono do arquivo pode ler a chave privada.
            using var fs = new FileStream(caminho, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var w = new StreamWriter(fs);
            w.Write(pem);
        }
        return new IdentidadeServidor(nova, caminho, nova: true);
    }

    /// <summary>Usa a chave que já existir (diretório atual ou pasta do programa); se não houver, cria no diretório atual.</summary>
    private static string Localizar()
    {
        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            string c = Path.Combine(dir, ArquivoPadrao);
            if (File.Exists(c)) return c;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), ArquivoPadrao);
    }

    public void Dispose() => Chave.Dispose();
}

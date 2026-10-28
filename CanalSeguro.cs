using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MrsChatServer;

/// <summary>
/// Canal cifrado entre cliente e servidor (protocolo MRS-CRIPTO/1). Usa só criptografia do .NET.
///
/// Aperto de mãos (uma linha de texto em cada sentido, antes de qualquer outra coisa):
///   cliente → servidor:  MRS-CRIPTO/1 C &lt;chave efêmera do cliente&gt;
///   servidor → cliente:  MRS-CRIPTO/1 S &lt;chave efêmera do servidor&gt; &lt;chave de identidade&gt; &lt;assinatura&gt;
///
///  - Chaves: ECDH P-256 efêmero (novo a cada conexão), em SubjectPublicKeyInfo, base64.
///  - Transcrição T = SHA-256("MRS-CRIPTO/1" ‖ 0x00 ‖ efêmeraCliente ‖ efêmeraServidor ‖ identidade).
///  - O servidor assina T com sua chave de identidade (ECDSA P-256/SHA-256, formato r‖s).
///    O cliente confere: isso prova que quem respondeu tem a chave cuja impressão digital ele vê.
///  - Segredo ECDH → HKDF-SHA-256 (sal = T) → uma chave AES-256 para cada sentido.
///
/// Depois disso, cada linha do protocolo antigo vai como base64(AES-256-GCM(linha)), com nonce
/// = contador da direção (0, 1, 2...). Uma linha alterada, repetida ou fora de ordem não decifra.
/// </summary>
sealed class CanalSeguro : IDisposable
{
    public const string Prefixo = "MRS-CRIPTO/1";
    public const string Algoritmos = "ECDH P-256 · AES-256-GCM · ECDSA";
    private const int TamanhoTag = 16;
    private static readonly string OidP256 = ECCurve.NamedCurves.nistP256.Oid.Value!;

    private readonly AesGcm _enviar;
    private readonly AesGcm _receber;
    private ulong _contadorEnvio;
    private ulong _contadorRecebimento;
    private readonly object _travaEnvio = new();
    private readonly object _travaRecebimento = new();

    private CanalSeguro(byte[] chaveEnvio, byte[] chaveRecebimento)
    {
        _enviar = new AesGcm(chaveEnvio, TamanhoTag);
        _receber = new AesGcm(chaveRecebimento, TamanhoTag);
        CryptographicOperations.ZeroMemory(chaveEnvio);
        CryptographicOperations.ZeroMemory(chaveRecebimento);
    }

    public static bool EhAbertura(string linha) => linha.StartsWith(Prefixo + " C ", StringComparison.Ordinal);

    // -------------------------------------------------------------- servidor

    /// <summary>Responde ao "olá" do cliente. Retorna o canal pronto e a linha (em texto puro) a enviar de volta.</summary>
    public static (CanalSeguro canal, string resposta) ResponderComoServidor(string abertura, ECDsa identidade)
    {
        string[] partes = abertura.Split(' ');
        if (partes.Length != 3 || partes[0] != Prefixo || partes[1] != "C")
            throw new CryptographicException("abertura inválida");

        byte[] efemeraCliente = Convert.FromBase64String(partes[2]);
        using var cliente = ImportarEcdh(efemeraCliente);
        using var minha = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] efemeraServidor = minha.ExportSubjectPublicKeyInfo();
        byte[] chaveIdentidade = identidade.ExportSubjectPublicKeyInfo();

        byte[] t = Transcricao(efemeraCliente, efemeraServidor, chaveIdentidade);
        byte[] assinatura = identidade.SignData(t, HashAlgorithmName.SHA256);
        byte[] segredo = minha.DeriveRawSecretAgreement(cliente.PublicKey);
        var (c2s, s2c) = DerivarChaves(segredo, t);

        string resposta = $"{Prefixo} S {Convert.ToBase64String(efemeraServidor)} {Convert.ToBase64String(chaveIdentidade)} {Convert.ToBase64String(assinatura)}";
        return (new CanalSeguro(chaveEnvio: s2c, chaveRecebimento: c2s), resposta);
    }

    // --------------------------------------------------------------- cliente

    /// <summary>Gera a chave efêmera do cliente e a linha de abertura.</summary>
    public static (ECDiffieHellman efemera, string abertura) IniciarComoCliente()
    {
        var e = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (e, $"{Prefixo} C {Convert.ToBase64String(e.ExportSubjectPublicKeyInfo())}");
    }

    /// <summary>Confere a resposta do servidor (inclusive a assinatura) e monta o canal.</summary>
    public static CanalSeguro ConcluirComoCliente(ECDiffieHellman efemera, string resposta, out byte[] chaveIdentidadeServidor)
    {
        string[] partes = resposta.Split(' ');
        if (partes.Length != 5 || partes[0] != Prefixo || partes[1] != "S")
            throw new CryptographicException("resposta de aperto de mãos inválida");

        byte[] efemeraServidor = Convert.FromBase64String(partes[2]);
        byte[] chaveIdentidade = Convert.FromBase64String(partes[3]);
        byte[] assinatura = Convert.FromBase64String(partes[4]);

        using var servidor = ImportarEcdh(efemeraServidor);
        using var identidade = ImportarEcdsa(chaveIdentidade);
        byte[] efemeraCliente = efemera.ExportSubjectPublicKeyInfo();
        byte[] t = Transcricao(efemeraCliente, efemeraServidor, chaveIdentidade);
        if (!identidade.VerifyData(t, assinatura, HashAlgorithmName.SHA256))
            throw new CryptographicException("assinatura do servidor não confere");

        byte[] segredo = efemera.DeriveRawSecretAgreement(servidor.PublicKey);
        var (c2s, s2c) = DerivarChaves(segredo, t);
        chaveIdentidadeServidor = chaveIdentidade;
        return new CanalSeguro(chaveEnvio: c2s, chaveRecebimento: s2c);
    }

    // ------------------------------------------------------------- mensagens

    public string Cifrar(string linha)
    {
        byte[] claro = Encoding.UTF8.GetBytes(linha);
        byte[] saida = new byte[claro.Length + TamanhoTag];
        lock (_travaEnvio)
        {
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _contadorEnvio++);
            _enviar.Encrypt(nonce, claro, saida.AsSpan(0, claro.Length), saida.AsSpan(claro.Length));
        }
        return Convert.ToBase64String(saida);
    }

    /// <summary>Decifra uma linha. Lança <see cref="CryptographicException"/> se foi alterada, repetida ou reordenada.</summary>
    public string Decifrar(string linha)
    {
        byte[] dados;
        try { dados = Convert.FromBase64String(linha); }
        catch (FormatException) { throw new CryptographicException("linha cifrada malformada"); }
        if (dados.Length < TamanhoTag) throw new CryptographicException("linha cifrada curta demais");

        byte[] claro = new byte[dados.Length - TamanhoTag];
        lock (_travaRecebimento)
        {
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _contadorRecebimento);
            _receber.Decrypt(nonce, dados.AsSpan(0, claro.Length), dados.AsSpan(claro.Length), claro);
            _contadorRecebimento++;
        }
        return Encoding.UTF8.GetString(claro);
    }

    // ----------------------------------------------------------------- apoio

    /// <summary>Impressão digital: SHA-256 da chave pública de identidade, em grupos de 4 hexadecimais.</summary>
    public static string Impressao(byte[] chavePublica, int grupos = 8)
    {
        string hex = Convert.ToHexString(SHA256.HashData(chavePublica));
        return string.Join(' ', Enumerable.Range(0, Math.Min(grupos, 16)).Select(i => hex.Substring(i * 4, 4)));
    }

    private static byte[] Transcricao(byte[] efemeraCliente, byte[] efemeraServidor, byte[] identidade)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.ASCII.GetBytes(Prefixo));
        h.AppendData([0]);
        h.AppendData(efemeraCliente);
        h.AppendData(efemeraServidor);
        h.AppendData(identidade);
        return h.GetHashAndReset();
    }

    private static (byte[] c2s, byte[] s2c) DerivarChaves(byte[] segredo, byte[] transcricao)
    {
        try
        {
            byte[] c2s = HKDF.DeriveKey(HashAlgorithmName.SHA256, segredo, 32, transcricao, Encoding.ASCII.GetBytes(Prefixo + " c2s"));
            byte[] s2c = HKDF.DeriveKey(HashAlgorithmName.SHA256, segredo, 32, transcricao, Encoding.ASCII.GetBytes(Prefixo + " s2c"));
            return (c2s, s2c);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(segredo);
        }
    }

    private static ECDiffieHellman ImportarEcdh(byte[] spki)
    {
        var k = ECDiffieHellman.Create();
        try
        {
            k.ImportSubjectPublicKeyInfo(spki, out int lidos);
            if (lidos != spki.Length || k.ExportParameters(false).Curve.Oid.Value != OidP256)
                throw new CryptographicException("chave efêmera inválida (esperado P-256)");
            return k;
        }
        catch { k.Dispose(); throw; }
    }

    private static ECDsa ImportarEcdsa(byte[] spki)
    {
        var k = ECDsa.Create();
        try
        {
            k.ImportSubjectPublicKeyInfo(spki, out int lidos);
            if (lidos != spki.Length || k.ExportParameters(false).Curve.Oid.Value != OidP256)
                throw new CryptographicException("chave de identidade inválida (esperado P-256)");
            return k;
        }
        catch { k.Dispose(); throw; }
    }

    public void Dispose()
    {
        _enviar.Dispose();
        _receber.Dispose();
    }
}

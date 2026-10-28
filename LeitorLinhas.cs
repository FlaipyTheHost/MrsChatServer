using System.Text;

namespace MrsChatServer;

/// <summary>
/// Lê linhas terminadas em '\n' de um stream, com limite de tamanho.
/// Evita os dois problemas da v1: mensagens grudadas/cortadas quando chegavam
/// juntas no mesmo pacote, e um cliente mal-intencionado mandando uma "linha" infinita.
/// </summary>
sealed class LeitorLinhas
{
    private readonly Stream _stream;
    private readonly int _tamanhoMaximo;
    private readonly byte[] _buffer = new byte[4096];
    private int _inicio;
    private int _fim;
    private readonly MemoryStream _linha = new();

    public LeitorLinhas(Stream stream, int tamanhoMaximo)
    {
        _stream = stream;
        _tamanhoMaximo = tamanhoMaximo;
    }

    /// <summary>Retorna a próxima linha (sem '\r\n'), ou null quando a conexão foi fechada.</summary>
    public async Task<string?> LerLinhaAsync(CancellationToken ct)
    {
        _linha.SetLength(0);
        while (true)
        {
            if (_inicio == _fim)
            {
                _inicio = 0;
                _fim = await _stream.ReadAsync(_buffer, ct);
                if (_fim == 0)
                {
                    // Conexão fechada: devolve o que sobrou, se houver.
                    return _linha.Length > 0 ? Decodificar() : null;
                }
            }

            int nl = Array.IndexOf(_buffer, (byte)'\n', _inicio, _fim - _inicio);
            if (nl >= 0)
            {
                if (_linha.Length + (nl - _inicio) > _tamanhoMaximo)
                    throw new InvalidDataException($"Linha maior que {_tamanhoMaximo} bytes.");
                _linha.Write(_buffer, _inicio, nl - _inicio);
                _inicio = nl + 1;
                return Decodificar();
            }

            _linha.Write(_buffer, _inicio, _fim - _inicio);
            _inicio = _fim;

            if (_linha.Length > _tamanhoMaximo)
                throw new InvalidDataException($"Linha maior que {_tamanhoMaximo} bytes.");
        }
    }

    private string Decodificar()
    {
        string s = Encoding.UTF8.GetString(_linha.GetBuffer(), 0, (int)_linha.Length);
        if (s.Length > _tamanhoMaximo) s = s[.._tamanhoMaximo];
        return s.TrimEnd('\r');
    }
}

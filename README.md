# <img src="icon.ico" width="40" height="auto"> MrsChatServer

[![Release](https://img.shields.io/github/v/release/FlaipyTheHost/MrsChatServer?label=release)](https://github.com/FlaipyTheHost/MrsChatServer/releases/latest)
[![Licença](https://img.shields.io/github/license/FlaipyTheHost/MrsChatServer)](LICENSE)

Servidor de salas para o [**MrsChat**](https://github.com/FlaipyTheHost/MrsChat). Ele hospeda uma sala para vários clientes conversarem na rede local ou pela internet, inclusive atrás de CGNAT e sem abrir porta no roteador. Tem criptografia obrigatória e um painel de central de comunicação no terminal.

<img src="captura.gif" alt="MrsChatServer em funcionamento">

## Recursos

- **Painel em tela cheia**: CPU do app e do sistema, RAM do app e do sistema, tráfego em KB com gráfico, operadores conectados, registro de eventos e console do operador.
- **A tela nunca mostra o texto das mensagens.** O tráfego recente aparece só como os bytes cifrados que passaram pela rede, e o mesmo vale para o log no modo `--simples`.
- **Criptografia obrigatória**: troca de chaves ECDH P-256 assinada pela chave de identidade do servidor, e depois AES-256-GCM em tudo.
- **Acesso pela internet com `--publico`**: um túnel [bore](https://github.com/ekzhang/bore) embutido que dá um endereço público, como `bore.pub:41234`, sem configurar o roteador.
- Senha da sala, limite de operadores, nomes únicos, mensagens privadas e comandos de administração (anunciar, listar, expulsar).
- **Não precisa do .NET instalado**: os pacotes da release trazem tudo.

## Instalação

Baixe o pacote do seu sistema na [página de releases](https://github.com/FlaipyTheHost/MrsChatServer/releases/latest).

| Sistema | Arquivo | Como instalar |
|---|---|---|
| **Windows** 10/11 | `MrsChatServer-<versão>-win-x64.zip` | Extraia e rode `MrsChatServer.exe` no Windows Terminal |
| **Ubuntu, Debian, Mint** | `mrschatserver_<versão>_amd64.deb` | `sudo apt install ./mrschatserver_<versão>_amd64.deb` |
| **Fedora, RHEL, openSUSE** | `mrschatserver-<versão>-1.x86_64.rpm` | `sudo dnf install ./mrschatserver-<versão>-1.x86_64.rpm` |
| **Outras distros Linux** | `mrschatserver-<versão>-linux-x64.tar.gz` | `tar xzf mrschatserver-*.tar.gz && cd mrschatserver-*/ && ./MrsChatServer` |

Os pacotes `.deb` e `.rpm` instalam o comando `mrschatserver`. Ele roda sempre dentro de `~/.config/MrsChatServer/`, onde ficam a configuração e a chave do servidor. Assim a identidade do servidor é a mesma, não importa de qual pasta você o chame.

## Primeiros passos

```bash
mrschatserver --sala "Favela do Rio"                     # rede local, porta 9000
mrschatserver --sala "Favela do Rio" --publico --senha s3gr3d0   # aberto pela internet
```

Com `--publico`, o registro mostra o endereço para passar aos amigos:

```
Túnel público ativo!
  Endereço para amigos fora da sua rede:  bore.pub:41234
```

Eles conectam com `mrschat --nome MrBlue --servidor bore.pub:41234 --senha s3gr3d0`.

Na primeira execução, o servidor cria sua **chave de identidade** e mostra a impressão digital no campo **CHAVE** do painel. Passe esse código aos seus amigos por outro meio, como o WhatsApp: é o mesmo que eles devem ver no painel VERIFICAÇÃO do cliente.

## Uso

| Opção | Descrição |
|---|---|
| `-p`, `--porta <n>` | Porta TCP local (padrão: 9000) |
| `--sala <nome>` | Nome da sala, mostrado no cabeçalho dos clientes |
| `--senha <texto>` | Exige senha para entrar |
| `--max-clientes <n>` | Limite de operadores (padrão: 50) |
| `--publico` | Abre um túnel público via `bore.pub` |
| `--relay <host[:porta]>` | Usa outro relay bore, por exemplo um VPS seu |
| `--relay-segredo <s>` | Segredo do relay, se ele exigir (`bore server --secret`) |
| `--porta-publica <n>` | Pede uma porta fixa no relay; senão, ela muda a cada reinício |
| `--chave <arquivo>` | Arquivo da chave de identidade (padrão: `MrsChatServer.chave`, criado se não existir) |
| `--aceitar-sem-cripto` | Aceita também clientes antigos, sem criptografia |
| `--simples` | Log linha a linha em vez do painel (usado também quando a saída é redirecionada) |
| `-h`, `--ajuda` / `-v`, `--versao` | Ajuda e versão |

A forma antiga `MrsChatServer <porta>` continua funcionando.

### Console do operador

No painel, digite na linha `ADMIN ▸`:

| Comando | Ação |
|---|---|
| `<texto>` | Envia um anúncio para todos |
| `/usuarios` | Lista quem está online |
| `/expulsar <nome> [motivo]` | Remove alguém da sala |
| `/endereco` | Mostra o endereço público |
| `/sair` | Encerra o servidor (ou Ctrl+C) |

### Arquivo de configuração

O `MrsChatServer.conf` fica na pasta onde o servidor roda: em `~/.config/MrsChatServer/` se foi instalado por pacote, ou na pasta extraída nas versões portáteis. As opções da linha de comando têm prioridade sobre ele.

```ini
porta=9000
sala=Favela do Rio
senha=
max_clientes=50
publico=nao
relay=bore.pub
relay_segredo=
porta_publica=0
chave=MrsChatServer.chave
aceitar_sem_cripto=nao
interface=painel
```

Quem instalou pelo `.deb` ou `.rpm` encontra um exemplo em `/usr/share/doc/mrschatserver/MrsChatServer.conf.exemplo`.

### Rodando como serviço (systemd)

```ini
# ~/.config/systemd/user/mrschatserver.service
[Unit]
Description=MrsChatServer
After=network-online.target

[Service]
ExecStart=/usr/bin/mrschatserver --simples --publico --porta-publica 41234
Restart=on-failure

[Install]
WantedBy=default.target
```

```bash
systemctl --user enable --now mrschatserver
journalctl --user -u mrschatserver -f        # acompanha o registro
loginctl enable-linger "$USER"                # continua rodando depois do logout
```

## Segurança

- **O que é protegido:** tudo entre cada cliente e o servidor (mensagens, nomes e senha da sala). Quem está no caminho, incluindo o relay `bore.pub`, não lê, não altera, não repete e não reordena nada. Uma linha adulterada derruba a conexão.
- **O que não é protegido:** não é criptografia ponta a ponta. O servidor decifra para repassar as mensagens. O painel e o log não mostram o conteúdo, mas quem controla o processo do servidor pode, tecnicamente, lê-lo.
- **Guarde o `MrsChatServer.chave`.** Ele é a chave privada que identifica o servidor e é criado só com permissão de leitura para o dono. Se ele for apagado, outro é gerado, e os clientes que já conheciam o servidor vão recusar a conexão até confirmarem a chave nova. Nunca coloque esse arquivo no Git (o `.gitignore` do projeto já o ignora).
- **Clientes antigos** (v2.2 ou anteriores) não cifram e são recusados. Com `--aceitar-sem-cripto` eles entram, a sala é avisada, e o tráfego deles vai em texto puro.
- **Sobre o `bore.pub`:** é um relay público e gratuito, sem garantia de disponibilidade. Para algo seu, rode `bore server --secret <segredo>` num VPS e use `--relay seu-vps --relay-segredo <segredo>`.

## Compilando

Precisa do [SDK do .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) ou mais novo.

```bash
git clone https://github.com/FlaipyTheHost/MrsChatServer.git
cd MrsChatServer
dotnet run -c Release -- --sala Teste
```

Para gerar um executável único, sem depender do .NET instalado:

```bash
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publicar/linux-x64
```

### Publicando uma release

O workflow [`.github/workflows/release.yml`](.github/workflows/release.yml) gera o `.zip` do Windows, o `.tar.gz`, o `.deb` e o `.rpm`, testa os executáveis e cria a release com os arquivos e o `SHA256SUMS.txt`:

```bash
git tag v2.3.0
git push origin v2.3.0
```

Ele também pode ser disparado à mão em **Actions → Release → Run workflow**. Nesse caso, os pacotes ficam como artefato da execução, sem criar release. O empacotamento é feito pelo [`empacotamento/empacotar.sh`](empacotamento/empacotar.sh), que também roda localmente.

## Licença

[GPL-3.0](LICENSE).

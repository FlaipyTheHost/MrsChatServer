# <img src="icon.ico" width="48" height="auto"> MrsChatServer

**MrsChatServer** é um aplicativo servidor que tem como objetivo hospedar uma sala para vários clientes MRS poderem conversar, podem ser adiquiridos [neste meu outro repositório](https://github.com/FlaipyTheHost/MrsChat).

# <img src="captura.gif">

Para se executar é necessário ter um ambiente **.Net Core** em sua máquina.

**Windows:**

1. Apenas instale as dependências .Net Core que podem ser adiquiridas a partir [deste link](https://dotnet.microsoft.com/pt-br/download/dotnet/thank-you/runtime-8.0.8-windows-x64-installer).

2. Execute o arquivo MrsChatServer.exe que você pode transferir e extrair a partir das [releases](https://github.com/FlaipyTheHost/MrsChatServer/releases/).

**Ubuntu/Mint/Debian:**

1. Apenas instale o pacote `dotnet-runtime-8`.
  ```
sudo apt update
sudo apt install dotnet-runtime-8 -y
  ```
2. Extraia o arquivo zipado de [releases](https://github.com/FlaipyTheHost/MrsChatServer/releases/) usando o `unzip` ou outro software de sua escolha e execute o **MrsChatServer**.
 ```
unzip MrsChatServer_x64_Release_*.zip
dotnet MrsChatServer.dll
 ```

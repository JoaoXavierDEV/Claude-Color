# claude-lamp

Faça uma lâmpada inteligente Wi‑Fi (Tuya / Elgin Smart / Smart Life) mudar de cor conforme o que o **Claude Code** está fazendo. Se ele te fez uma pergunta, pediu permissão ou está esperando você aprovar o plano, a lâmpada pisca. Quando ele termina, ela fica verde. Assim dá para fazer outra coisa e saber, de longe, quando o Claude precisa de você.

Opcionalmente, o **RGB do PC acompanha o sinal**: memória RAM e placa-mãe (Corsair iCUE), teclado e mouse (Logitech G HUB), placa de vídeo MSI (Mystic Light) e fans ARGB (OpenRGB). Terminado o sinal, cada software volta ao próprio perfil.

Também serve para controlar a lâmpada pelo PC: liga/desliga, cor, brilho e branco quente/frio, tudo pela **rede local**, sem passar pela nuvem.

Feito em **.NET 10 / Windows Forms**, para Windows. É um único executável, que fica na bandeja do sistema e tem uma janela de configuração.

## Sinais padrão

| Evento do Claude Code | Hook | Lâmpada |
|---|---|---|
| Pergunta para você | `PreToolUse` → `AskUserQuestion` | 🟡 amarelo, pisca 3× |
| Plano aguardando aprovação | `PreToolUse` → `ExitPlanMode` | 🟣 roxo, pisca 3× |
| Pedido de permissão | `Notification` (`permission_prompt`) | 🟠 laranja, pisca 3× |
| Terminou | `Stop` | 🟢 verde, volta à sua cor após 60 s |
| Trabalhando | `UserPromptSubmit` | 🔵 azul fraco |
| Ocioso (esperando você) | `Notification` (`idle_prompt`) | desativado por padrão |
| *(interno)* você agiu | `PostToolUse` | encerra o "aguardar intervenção" |

Cor, brilho, número de piscadas, intervalo e tempo para restaurar podem ser mudados por evento, na aba **Eventos do Claude**.

Na coluna **Manter** da mesma aba há duas opções:
- **Trabalhando → "enquanto o Claude trabalha"**: a cor fica acesa do envio da sua mensagem até o Claude terminar (`Stop`). Perguntas, permissões e planos no meio mostram a cor deles e depois voltam para a de trabalhando.
- **Pergunta, plano, permissão e ocioso → "aguardar intervenção"**: a cor fica acesa até você agir (responder, aprovar, permitir ou mandar outra mensagem), em vez de restaurar depois de um tempo fixo. A intervenção é detectada pelo hook `PostToolUse`, disparado quando a ferramenta conclui.

As duas opções também se encerram por uma mudança manual sua e por um limite de segurança de 30 min, caso o Claude feche sem avisar.

## Compatibilidade

- Lâmpadas com firmware **Tuya**. Isso inclui Elgin Smart (por exemplo, a Smart Color A70 15 W RGB), lâmpadas que funcionam no app Smart Life / Tuya Smart e muitas marcas "white label".
- Protocolo local **3.3, 3.4 e 3.5**, com implementação própria. A 3.1 funciona via [TuyaNet](https://github.com/ClusterM/tuyanet).
- Os dois formatos de DPs de lâmpada: v2 (DPs 20–24, o mais comum) e v1 (DPs 1–5).

## Instalação

Requisito: [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/JoaoXavierDEV/claude-lamp.git
cd claude-lamp
dotnet publish src/ClaudeLamp.App -c Release -o "%USERPROFILE%\.claude-lamp\bin"
"%USERPROFILE%\.claude-lamp\bin\ClaudeLamp.exe"
```

Na primeira execução, a janela abre na aba **Conexão**.

## Configuração

### 1. Obter a *local key*

O controle local precisa do **Device ID** e da **local key** da lâmpada. A local key é obtida uma única vez pela nuvem da Tuya:

1. Crie uma conta em **[platform.tuya.com](https://platform.tuya.com)**. O endereço antigo, iot.tuya.com, pode não abrir.
2. Vá em **Cloud → Development → Create Cloud Project**, com *Industry* = Smart Home, *Development Method* = Smart Home e o data center da sua conta (para o Brasil, **Western America**).
3. No projeto, abra **Devices → Link App Account → Add App Account → Tuya App Account Authorization** e escaneie o QR code pelo app **Smart Life**: aba **Eu**, ícone de scanner.
   > ⚠️ O app **Elgin Smart** (e outros apps de marca própria) não autoriza esse QR code: ele aparece como *"QR code has expired"*. Remova a lâmpada do app Elgin, adicione no **Smart Life** e escaneie por ele.
4. Copie o **Access ID** e o **Access Secret** na aba **Overview** do projeto.
5. No ClaudeLamp, aba **Conexão**, use **Buscar local key na nuvem**: informe região, Access ID e Secret, clique em *Buscar dispositivos* e depois em *Usar selecionado*. O Secret não é salvo.

> Se você parear a lâmpada de novo em algum app, a local key muda e esse passo precisa ser repetido.

### 2. Conectar

Na aba **Conexão**:
- **Procurar na rede** encontra o IP da lâmpada. Primeiro escuta os anúncios UDP; se o roteador os bloquear, procura dispositivos com a porta TCP 6668 aberta.
- **Testar / detectar protocolo** tenta 3.5 → 3.4 → 3.3 → 3.1 e salva a versão que responder.

### 3. Instalar os hooks

Na aba **Sistema**, clique em **Instalar / atualizar hooks**. O app adiciona as entradas em `~/.claude/settings.json` (fazendo backup antes) e preserva os hooks de outras ferramentas. Sessões do Claude Code que já estavam abertas podem precisar ser reiniciadas.

Exemplo do que é adicionado:

```json
{
  "hooks": {
    "Stop": [
      { "hooks": [ { "type": "command", "command": "\"C:\\...\\ClaudeLamp.exe\" hook", "timeout": 10 } ] }
    ],
    "PreToolUse": [
      { "matcher": "AskUserQuestion|ExitPlanMode",
        "hooks": [ { "type": "command", "command": "\"C:\\...\\ClaudeLamp.exe\" hook", "timeout": 10 } ] }
    ]
  }
}
```

### 4. RGB do PC (opcional)

Durante um sinal, o ClaudeLamp assume a iluminação dos softwares abaixo e depois a devolve: o iCUE retoma o perfil dele; o G HUB, o Mystic Light e o OpenRGB voltam exatamente ao modo e às cores que estavam antes.

| Software | Dispositivos | SDK oficial (DLL x64) |
|---|---|---|
| Corsair iCUE | RAM, placa-mãe e o que mais o iCUE expuser | [iCUE SDK 4.x](https://github.com/CorsairOfficial/cue-sdk/releases): `iCUESDK.x64_2019.dll` |
| Logitech G HUB | teclado, mouse e periféricos LIGHTSYNC | [LED SDK](https://www.logitechg.com/sdk/LED_SDK_9.00.zip): `LogitechLedEnginesWrapper.dll` |
| MSI Center (Mystic Light) | placa de vídeo / placa-mãe MSI | `MysticLight_SDK_x64.dll`, já instalada com o plugin MSI do iCUE ou com o SDK do MSI Center. **Exige o app como administrador.** |
| [OpenRGB](https://openrgb.org) 1.x | o que você marcar, por exemplo fans de um controlador Cooler Master ARGB | servidor SDK na porta 6742 (o ClaudeLamp inicia o OpenRGB sozinho) |

1. Baixe os SDKs e coloque as DLLs **x64** em `<pasta do app>\x64\`, por exemplo `%USERPROFILE%\.claude-lamp\bin\x64\`. Elas não são distribuídas neste repositório.
2. No **iCUE**, abra Configurações → Geral → **Habilitar SDK**. No **G HUB**, permita que jogos e aplicativos controlem a iluminação.
3. **OpenRGB**: instale (`winget install OpenRGB.OpenRGB`). Na aba **Dispositivos RGB**, clique em *Carregar dispositivos do OpenRGB* e marque só o que nenhum outro software controla, para evitar disputa. Por padrão vêm marcados só os que não são RAM, placa-mãe, GPU, teclado ou mouse.
4. **GPU MSI**: marque *MSI Mystic Light* e, na aba **Sistema**, use *Reiniciar como administrador* e *Iniciar com o Windows, como administrador* (Tarefa Agendada `ClaudeLamp`, sem UAC a cada login).
5. Na aba **Dispositivos RGB**, use *Detectar* e *Testar*. Na aba **Eventos**, a coluna **PC RGB** escolhe quais eventos usam o RGB do PC. Por padrão, todos usam, menos "trabalhando" e "ocioso".

O RGB do PC só funciona com o app aberto na bandeja, porque é ele que segura o controle durante o sinal. Com o app fechado, os hooks acendem só a lâmpada.

Detalhes:
- **GPU MSI**: cada chamada ao Mystic Light leva centenas de milissegundos. Por isso a GPU não pisca: ela recebe a cor final logo no início do sinal. O estilo de cor fixa é detectado por dispositivo ("Steady" nas placas-mãe, "NoAnimation" nas placas de vídeo).
- **OpenRGB**: no OpenRGB 1.x, clientes do SDK com protocolo menor que 6 têm a troca de modo ignorada ([issue 5924](https://gitlab.com/CalcProgrammer1/OpenRGB/-/work_items/5924)). Por isso o ClaudeLamp traz um cliente próprio do protocolo 6 (`OpenRgbProtocol.cs`), que consegue voltar o dispositivo ao modo original (por exemplo, *Spectrum*).
- Se os fans também forem controlados pelo **MasterCTRL** (Cooler Master), deixe a iluminação deles só com o OpenRGB, ou os dois vão disputar o controle.

## Uso

- **Bandeja**: clique esquerdo liga/desliga; clique direito mostra cores, branco, brilho e *Sinais do Claude Code* (para pausar os sinais). Um duplo clique abre as configurações. O ícone assume a cor do último sinal.
- **Janela**:
  - **Controle**: cor, brilho e branco.
  - **Eventos do Claude**: uma linha por evento, com *Manter* (enquanto trabalha / aguardar intervenção) e o botão *Testar*, que sempre volta ao normal em 5 s.
  - **Dispositivos RGB**: iCUE, G HUB, Mystic Light e OpenRGB (com lista de dispositivos), com *Detectar* e *Testar*.
  - **Conexão**: Device ID, IP, local key e protocolo.
  - **Sistema**: hooks, *Reiniciar como administrador*, *Iniciar com o Windows* (Tarefa Agendada elevada), log e config.
- Fechar a janela não encerra o app, que continua na bandeja. Para sair, use **Sair** no menu da bandeja.

## Como funciona

```
Claude Code ──hook (JSON no stdin)──► ClaudeLamp.exe hook ──► mapeia o evento (retorna em ~150 ms)
                                            │
                     app na bandeja aberto? ├─ sim ─► named pipe "ClaudeLamp.Signals" ─► app da bandeja
                                            │                                            ├─► lâmpada (TCP 6668)
                                            │                                            ├─► iCUE SDK
                                            │                                            ├─► Logitech LED SDK
                                            │                                            ├─► MSI Mystic Light SDK (admin)
                                            │                                            └─► OpenRGB (SDK protocolo 6)
                                            └─ não ─► ClaudeLamp.exe run-signal (processo separado) ─► só a lâmpada
```

- O hook nunca falha e nunca bloqueia: ele sempre sai com código 0, e o efeito roda no app da bandeja ou num processo separado (via ShellExecute, sem herdar os pipes do Claude Code).
- Os alvos recebem o sinal em paralelo, e a falha de um não interrompe os outros. Cada SDK do PC tem uma fila própria em que o comando mais recente vence: um dispositivo lento descarta os passos intermediários do piscar e não atrasa os outros.
- O pipe aceita só o usuário atual e tem rótulo de integridade média. Assim, o hook (sem admin) consegue falar com o app mesmo quando ele roda como administrador.
- Cada sinal recebe um número de geração, e um sinal mais novo cancela o restaurar de um antigo.
- Um aviso de permissão que chega logo depois de uma pergunta ou de um plano é ignorado, para não trocar a cor mais específica.
- Configuração, estado e log ficam em `%APPDATA%\ClaudeLamp\` (`config.json`, `state.json` e `log.txt`). A variável `CLAUDE_LAMP_HOME` muda esse diretório.

### Estrutura

```
src/ClaudeLamp.Core          protocolo Tuya, descoberta, cores, config, hooks
  TuyaLocalTransport.cs        protocolo local 3.3 / 3.4 / 3.5 (sessão HMAC + AES-ECB/GCM)
  TuyaDiscovery.cs             broadcasts UDP 6666/6667/7000 + varredura TCP 6668
  LampClient.cs                operações de alto nível e detecção de protocolo
  ClaudeHookEvents.cs          evento do hook → sinal; execução dos sinais
  LightTargets.cs              ILightTarget (lâmpada, RGB do PC)
  SignalPipe.cs                canal hook → app
  HookHandler.cs, HooksInstaller.cs, CloudKeys.cs
src/ClaudeLamp.Rgb           RGB do PC: Corsair (RGB.NET), Logitech e MSI (P/Invoke), OpenRGB (cliente próprio, protocolo 6)
src/ClaudeLamp.App           Windows Forms: bandeja + janela (abas) + SignalHost
tests/ClaudeLamp.Core.Tests  xUnit
```

### Desenvolvimento

```bash
dotnet build
dotnet test
```

## Solução de problemas

| Sintoma | O que verificar |
|---|---|
| *"QR code has expired"* ao vincular | Use o app **Smart Life**, não o Elgin Smart. |
| *"sign invalid"* ao buscar a local key | Access ID ou Secret copiados errado. Copie de novo pelo ícone de copiar na aba Overview. |
| *Procurar na rede* não acha nada pelo UDP | Normal em alguns roteadores; a busca por TCP 6668 cobre esse caso. Para o UDP, libere o `ClaudeLamp.exe` no firewall (rede privada). |
| Timeout / nenhum protocolo responde | Feche o app Smart Life no celular (a lâmpada aceita poucas conexões locais ao mesmo tempo). Confira também se a local key é do pareamento atual. |
| RAM / placa-mãe não acendem | Confira "Habilitar SDK" no iCUE e se `iCUESDK.x64_2019.dll` aparece como encontrado na aba **Dispositivos RGB**. |
| GPU MSI não muda | O app precisa estar como administrador (aba **Sistema**). O log mostra `Mystic Light não inicializou` quando não está. |
| Fans não voltam ao modo original | Confira se o MasterCTRL (ou outro software) não está reaplicando as cores, e se o log mostra `openrgb: ... restaurado para '...'`. |
| Teclado / mouse não acendem | Confira se o G HUB está aberto e permite o controle por aplicativos. Se a *Iluminação dinâmica* do Windows estiver ligada para esses dispositivos, desligue-a. |
| A lâmpada não reage ao Claude Code | Veja se os hooks aparecem como "Instalados" na aba **Sistema**, reinicie a sessão do Claude Code e consulte `log.txt`. |

## Créditos

- [TinyTuya](https://github.com/jasonacox/tinytuya): referência do protocolo local Tuya.
- [TuyaNet](https://github.com/ClusterM/tuyanet): cliente da API cloud (busca da local key) e do protocolo 3.1.
- [RGB.NET](https://github.com/DarthAffe/RGB.NET): integração com o iCUE SDK e referência das assinaturas do Mystic Light SDK.
- [OpenRGB](https://openrgb.org): servidor e especificação do SDK.

## Licença

[MIT](LICENSE). Os SDKs de terceiros (Corsair iCUE SDK, Logitech LED SDK, MSI Mystic Light SDK) não fazem parte deste repositório e seguem as licenças dos respectivos fabricantes.

Projeto independente, sem vínculo com a Elgin, a Tuya, a Corsair, a Logitech, a MSI, a Cooler Master ou a Anthropic.

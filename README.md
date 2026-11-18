# claude-lamp

Faça uma lâmpada inteligente Wi‑Fi (Tuya / Elgin Smart / Smart Life) mudar de cor conforme o que o **Claude Code** está fazendo. Se ele te fez uma pergunta, pediu permissão ou está esperando você aprovar o plano, a lâmpada pisca. Quando ele termina, ela fica verde. Assim dá para fazer outra coisa e saber, de longe, quando o Claude precisa de você.

Opcionalmente, o **RGB do PC acompanha o sinal**: memória RAM e placa-mãe (Corsair iCUE), teclado e mouse (Logitech G HUB). Terminado o sinal, cada software volta ao próprio perfil.

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

Cor, brilho, número de piscadas, intervalo e tempo para restaurar podem ser mudados por evento, na aba **Eventos do Claude**.

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

Durante um sinal, o ClaudeLamp assume a iluminação dos softwares abaixo e depois a devolve: o iCUE retoma o perfil dele, e o G HUB restaura exatamente o que estava antes.

| Software | Dispositivos | SDK oficial (DLL x64) |
|---|---|---|
| Corsair iCUE | RAM, placa-mãe e o que mais o iCUE expuser | [iCUE SDK 4.x](https://github.com/CorsairOfficial/cue-sdk/releases): `iCUESDK.x64_2019.dll` |
| Logitech G HUB | teclado, mouse e periféricos LIGHTSYNC | [LED SDK](https://www.logitechg.com/sdk/LED_SDK_9.00.zip): `LogitechLedEnginesWrapper.dll` |

1. Baixe os SDKs e coloque as DLLs **x64** em `<pasta do app>\x64\`, por exemplo `%USERPROFILE%\.claude-lamp\bin\x64\`. Elas não são distribuídas neste repositório.
2. No **iCUE**, abra Configurações → Geral → **Habilitar SDK**. No **G HUB**, permita que jogos e aplicativos controlem a iluminação.
3. Na aba **Dispositivos RGB**, use *Detectar* e *Testar*. Na aba **Eventos**, a coluna **PC RGB** escolhe quais eventos usam o RGB do PC. Por padrão, todos usam, menos "trabalhando" e "ocioso".

O RGB do PC só funciona com o app aberto na bandeja, porque é ele que segura o controle durante o sinal. Com o app fechado, os hooks acendem só a lâmpada.

> Ainda não suportados: placa de vídeo **MSI** (o Mystic Light SDK exige rodar como administrador) e fans controlados pelo **Cooler Master MasterCTRL** (não há SDK público).

## Uso

- **Bandeja**: clique esquerdo liga/desliga; clique direito mostra cores, branco, brilho e *Sinais do Claude Code* (para pausar os sinais). Um duplo clique abre as configurações. O ícone assume a cor do último sinal.
- **Janela**:
  - **Controle**: cor, brilho e branco.
  - **Eventos do Claude**: uma linha por evento, com botão *Testar*.
  - **Dispositivos RGB**: iCUE e G HUB, com *Detectar* e *Testar*.
  - **Conexão**: Device ID, IP, local key e protocolo.
  - **Sistema**: hooks, *Iniciar com o Windows*, log e config.
- Fechar a janela não encerra o app, que continua na bandeja. Para sair, use **Sair** no menu da bandeja.

## Como funciona

```
Claude Code ──hook (JSON no stdin)──► ClaudeLamp.exe hook ──► mapeia o evento (retorna em ~150 ms)
                                            │
                     app na bandeja aberto? ├─ sim ─► named pipe "ClaudeLamp.Signals" ─► app da bandeja
                                            │                                            ├─► lâmpada (TCP 6668)
                                            │                                            ├─► iCUE SDK
                                            │                                            └─► Logitech LED SDK
                                            └─ não ─► ClaudeLamp.exe run-signal (processo separado) ─► só a lâmpada
```

- O hook nunca falha e nunca bloqueia: ele sempre sai com código 0, e o efeito roda no app da bandeja ou num processo separado (via ShellExecute, sem herdar os pipes do Claude Code).
- Os alvos (lâmpada, iCUE, G HUB) recebem o sinal em paralelo, e a falha de um não interrompe os outros.
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
src/ClaudeLamp.Rgb           RGB do PC: Corsair (RGB.NET) e Logitech (LED SDK via P/Invoke)
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
| Teclado / mouse não acendem | Confira se o G HUB está aberto e permite o controle por aplicativos. Se a *Iluminação dinâmica* do Windows estiver ligada para esses dispositivos, desligue-a. |
| A lâmpada não reage ao Claude Code | Veja se os hooks aparecem como "Instalados" na aba **Sistema**, reinicie a sessão do Claude Code e consulte `log.txt`. |

## Créditos

- [TinyTuya](https://github.com/jasonacox/tinytuya): referência do protocolo local Tuya.
- [TuyaNet](https://github.com/ClusterM/tuyanet): cliente da API cloud (busca da local key) e do protocolo 3.1.
- [RGB.NET](https://github.com/DarthAffe/RGB.NET): integração com o iCUE SDK.

Projeto independente, sem vínculo com a Elgin, a Tuya, a Corsair, a Logitech ou a Anthropic.

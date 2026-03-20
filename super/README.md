# Window Manager Broadcast

Base inicial de arquitetura para um aplicativo Windows com foco em:

- gerenciar multiplas janelas
- criar instancias isoladas de navegador embarcado
- visualizar e interagir com cada janela no painel central
- descobrir destinos de exibicao na rede
- encaminhar cada janela para uma TV diferente

## Observacao importante

O requisito "Miracast" precisa ser separado do requisito "TVs rastreadas na rede cabeada".

- `Miracast` normalmente funciona sobre `Wi-Fi Direct`
- TVs em rede cabeada geralmente exigem outro transporte, como `WebRTC`, `RTSP`, `NDI` ou um protocolo proprietario do fabricante

Por isso, esta base organiza a aplicacao com uma camada de `DisplayTransport`, permitindo suportar:

- `Miracast` quando o destino for compativel no Windows
- `LanStreaming` para TVs e players acessiveis pela rede cabeada

## Estrutura criada

- `docs/arquitetura.md`: visao tecnica do sistema
- `src/WindowManager.Core`: modelos e contratos principais
- `src/WindowManager.App`: aplicativo WPF de MVP compilavel
- `build.ps1`: script para criar a solucao e executar restore/build

## Fluxo esperado

1. O operador abre o gerenciador.
2. O sistema descobre TVs, dongles e receptores disponiveis.
3. O operador cria uma nova janela de navegador.
4. A janela aparece no painel principal com preview e controle.
5. O operador seleciona um destino de exibicao.
6. O sistema inicia a captura daquela janela e a transmite para o destino escolhido.

## Como compilar

1. Instale o `.NET 8 SDK`.
2. Execute:

```powershell
.\build.ps1 -Restore -Build
```

## Teste com o app Roku

1. Abra `Abrir-App.cmd`.
2. No app Windows, confirme que a porta esta em `8090`.
3. Crie uma janela apontando para `https://emei.lovable.app`.
4. Se quiser expor a rota local HTML, habilite a publicacao da janela.
5. Com o app aberto, o servidor local responde:

```text
http://IP_DO_WINDOWS:8090/health
http://IP_DO_WINDOWS:8090/api/windows
```

6. Instale o pacote `hello-roku.zip` na TV Roku.

## Atualizacao automatica de TVs em dev mode

Se quiser que o `super` empurre atualizacoes do canal Roku automaticamente para TVs em modo desenvolvedor:

1. copie `roku-devices.example.json` para `roku-devices.json`
2. preencha `deviceId`, `host`, `username` e `password`
3. mantenha `hello-roku.zip` atualizado na raiz do monorepo

Quando uma TV Roku se registrar no bridge com versao antiga do canal, o `super` tenta fazer sideload automatico do pacote via developer mode.
7. Abra o app Roku na TV.
8. O app deve listar a janela `emei.lovable.app` vinda do servidor Windows.

Observacao:

- este teste valida a integracao Windows -> Roku por HTTP
- o streaming real de video para a Roku ainda nao foi implementado

## Proxima etapa recomendada

Para transformar esta base em aplicacao operacional, o caminho mais consistente e:

1. manter o `.NET SDK` para Windows Desktop
2. escolher a pilha de navegador embarcado:
   - `CefSharp` se CEF for obrigatorio
   - `WebView2` se aceitarmos um primeiro MVP mais simples
3. implementar captura por janela com `Windows Graphics Capture`
4. escolher o protocolo real de saida em rede cabeada

## Status desta entrega

Esta entrega inclui uma base WPF compilavel, mas o build nao foi validado localmente porque a maquina possui runtime .NET e nao possui o SDK instalado.

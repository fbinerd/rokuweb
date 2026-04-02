# Handoff

## Estado Atual (Branch: experiments/roku-interactive-next)
- **Merge Base:** Esta branch divergiu da `develop` no commit **`8649fa3`** (Tag `v0.1.0`).
- Projeto WPF híbrido: Localmente em `.NET Framework 4.8.1` (binários legado), CI preparado para `.NET 10`.
- **Interactive Bridge Operacional:** A TV agora não é apenas um monitor, mas um terminal de entrada. Endpoint `/api/control` injeta eventos de mouse/teclado no CEF.
- **Streaming HLS Dinâmico:** Implementado `BrowserPanelRollingHlsService` para broadcast de janelas em tempo real via playlists `.m3u8`.
- **Integração Inteligente YouTube:** Uso de `yt-dlp.exe` para extrair streams diretos, contornando o overhead de renderização do player web no Roku.
- **Modos de Streaming:** Suporte a `Interacao` (latência mínima) e `Video` (estabilidade HLS), com troca de modo coordenada entre Windows e TV via ACKs.
- **Automação de Ciclo de Vida Roku:** Gerenciamento de energia (ECP PowerOn/Off) e Sideload automático de canais via `RokuDevDeploymentService`.
- **Sistema de Perfis Avançado:** Persistência completa de `TvProfiles` (IPs/MACs) e `WindowProfiles` (Nickname/URL/Modo/Perfil de Navegador).

## Funcionalidades de Vídeo Avançadas
- `LocalWebRtcPublisherService`: Agora é o servidor central de rotas dinâmicas, servindo HLS, thumbnails e API de comando.
- `DirectVideoOverlay`: Sistema de detecção de vídeo que normaliza coordenadas e resolve qualidades (720p, 480p, 360p) para o `Video Node` da Roku.
- `StreamReloadVersions`: Sistema de controle de versão de stream para forçar refresh na TV após interações críticas.

## Automação e DevLoop
- `Dev-LocalBuildAndSideload.ps1`: Script mestre que faz build, gera o `.zip` da Roku com timestamp local e faz o deploy via porta 80.
- `Start-LocalDiagnosticsMonitor`: Monitor de logs unificado que funde a saída do SuperPainel com o Telnet (porta 8085) da Roku.
- `build-monorepo.yml`: Pipeline completo com Delta Patches e publicação em GitHub Pages.

## Arquivos-Chave do "Interactive Next"
- `src/WindowManager.App/Runtime/Publishing/LocalWebRtcPublisherService.cs`: O coração do servidor de streaming e controle.
- `src/WindowManager.App/ViewModels/MainViewModel.cs`: Orquestração de sessões ativas e comandos de hardware.
- `src/WindowManager.App/Runtime/Publishing/BrowserPanelRollingHlsService.cs`: Engine de geração de segmentos de vídeo.
- `tools/yt-dlp/yt-dlp.exe`: Dependência externa para extração de vídeo direto.

## Decisões de Engenharia
- **Porta 8090:** Padronizada para evitar conflitos com a porta 8088 de testes anteriores.
- **Coordenadas Normalizadas (0.0 a 1.0):** Implementadas para garantir que o clique na TV (independente da resolução) caia no lugar certo do CEF.
- **Supressão de DirectVideo no Modo Interação:** O sistema desabilita o bypass de YT quando o usuário está interagindo para evitar loops de UI.
- **Persistence-First:** Toda alteração de perfil dispara um `QueueAutoSave` para evitar perda de configuração de janelas.

## Limitacoes conhecidas
- **Áudio:** O áudio via HLS ainda apresenta dessincronização em relação ao vídeo em redes com jitter alto.
- **Bypass de YouTube:** Depende da atualização constante do `yt-dlp.exe` para não quebrar com mudanças no player do Google.

## Proximos passos recomendados
1. **Sincronização A/V:** Ajustar os timestamps do `BrowserAudioHlsService` com o `BrowserPanelRollingHlsService`.
2. **Roku Video Node:** Atualizar o BrightScript do `rokuweb` para gerenciar a transição suave entre o modo `Interacao` (poster/image) e `Video` (Video Node).
3. **UI de Edição de Janelas:** Adicionar controles na UI para forçar o `RequestStreamReload` manualmente.
4. **Keep-Alive:** Refinar o `EnsureKeepAliveStreamsAsync` para evitar que a TV entre em sleep durante sessões longas de vídeo.

## Como Compilar
```powershell
.\build.ps1 -Restore -Build
```

## Como Testar (Local Loop)
```powershell
.\Dev-LocalBuildAndSideload.ps1 -RokuIp "SEU_IP_AQUI" -LaunchSuper -LaunchRokuApp
```

## Prompt de Retomada
```text
Trabalhando na branch experiments/roku-interactive-next.
Status: Bridge interativa e HLS H.264 funcionais. yt-dlp integrado.
Foco: Refinar a transição automática de modos no lado da Roku.
```

## Como compilar
```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Restore -Build
```

## Como rodar
```powershell
.\src\WindowManager.App\bin\Release\net481\WindowManager.App.exe
```

## Prompt de retomada sugerido
```text
Continue a partir do commit 8649fa3 na branch main.
Estado atual: CEF funcionando, LinkRTC funcionando, perfis persistindo janelas, TVs, paines e configuracoes especificas de painel.
Proximo passo: expor e editar na UI as configuracoes dos paines estaticos.
```

# Handoff

## Estado atual
- Projeto WPF para Windows em `.NET Framework 4.8.1`.
- CEF voltou a funcionar no app.
- Navegacao por janela funcionando.
- Previews simultaneos funcionando.
- Clique duplo abre janela ampliada.
- Menu de contexto do painel inclui configuracoes e `Ir para LinkRTC`.
- `Ir para LinkRTC` agora publica automaticamente e abre via `localhost`.
- Pagina LinkRTC abre limpa, sem a tarja preta.
- Perfil persiste porta do servidor, modo de acesso, IP especifico, janelas, paines estaticos, TVs persistidas e configuracoes especificas de painel.
- Perfil padrao de abertura suportado.

## Commits recentes importantes
- `8649fa3` feat: persist display targets and panel-specific profile state
- `6c3f64a` fix: remove linkrtc overlay banner
- `c91f1cc` fix: auto-publish and open linkrtc via localhost
- `149141a` fix: point cef locales path to output locales folder
- `3608536` fix: remove unsupported cef settings members
- `42903d9` fix: align cef startup with nullable api
- `79d79f8` fix: re-enable cef startup initialization
- `3322c32` fix: replace linkrtc listener with tcp server
- `7d27c66` fix: make panel deletion visible and stop reopening last profile

## Tag util
- `v0.1.0`

## Arquivos-chave
- `src/WindowManager.App/App.xaml.cs`
- `src/WindowManager.App/ViewModels/MainViewModel.cs`
- `src/WindowManager.App/Runtime/Publishing/LocalWebRtcPublisherService.cs`
- `src/WindowManager.App/Runtime/Publishing/LinkRtcAddressBuilder.cs`
- `src/WindowManager.App/Profiles/AppProfile.cs`
- `src/WindowManager.App/ViewModels/StaticDisplayPanelViewModel.cs`

## Decisoes importantes
- O app usa CEF embutido, nao WebView2.
- O servidor do LinkRTC nao usa mais `HttpListener`; usa `TcpListener` para evitar dependencia de `urlacl`.
- O clique em `Ir para LinkRTC` abre a rota local em `localhost`, mesmo quando o modo publico estiver em `Lan` ou `SpecificIp`.
- O startup nao reabre mais automaticamente o ultimo perfil; sem perfil padrao, abre `default`.

## Limitacoes conhecidas
- A parte "WebRTC" hoje publica uma pagina local com iframe da URL da janela; ainda nao e streaming real de frames.
- Descoberta de TVs continua em camada simulada/preparada, nao integracao Miracast real do Windows.
- As configuracoes especificas dos paines estaticos ja persistem, mas ainda nao estao plenamente expostas para edicao dedicada na UI.

## Proximos passos recomendados
1. Expor na UI as configuracoes especificas de cada painel estatico.
2. Permitir editar painel estatico: apelido, janela preferida, nickname de rota, WebRTC ligado/desligado.
3. Refinar o fluxo de LinkRTC para cenarios de acesso por IP externo.
4. Evoluir de pagina local para pipeline real de captura/transmissao, se esse continuar sendo o objetivo.

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

# Plano Minimo Para Exibir `emei.lovable.app` No Roku

## Objetivo

Permitir que o `superWebRTCStream` carregue `https://emei.lovable.app` em uma janela CEF no Windows e disponibilize esse conteudo para o `rokuweb` em um formato que a Roku consiga reproduzir.

## Situacao Atual

Hoje ja existe:

- navegador embutido com CEF no `superWebRTCStream`
- servidor HTTP local simples via `TcpListener`
- publicacao de rotas HTML por janela
- app `rokuweb` capaz de buscar JSON por HTTP

Hoje ainda nao existe:

- endpoint `/api/windows` no `superWebRTCStream`
- endpoint de stream para Roku
- pipeline real de captura de video
- HLS (`.m3u8` + segmentos`)
- player de video no `rokuweb`

## Arquitetura Minima Recomendada

```text
emei.lovable.app
        |
        v
CEF Window no superWebRTCStream
        |
        v
Captura da janela
        |
        v
Encoder H.264 + geracao HLS
        |
        v
Servidor HTTP local no superWebRTCStream
        |-- GET /api/windows
        |-- GET /streams/{slug}/index.m3u8
        |-- GET /streams/{slug}/{segment}.ts
        |
        v
rokuweb
        |
        v
Video node do Roku reproduzindo HLS
```

## Contrato Minimo Entre Windows E Roku

### 1. Descoberta/listagem

O `rokuweb` precisa consultar:

`GET http://10.1.0.10:8090/api/windows`

Resposta proposta:

```json
{
  "windowCount": 1,
  "windows": [
    {
      "id": "emei-main",
      "title": "EMEI",
      "state": "Streaming",
      "initialUrl": "https://emei.lovable.app",
      "streamUrl": "http://10.1.0.10:8090/streams/emei-main/index.m3u8"
    }
  ]
}
```

### 2. Stream de video

O `rokuweb` deve consumir:

- `GET /streams/{slug}/index.m3u8`
- `GET /streams/{slug}/{segment}.ts`

Formato minimo recomendado:

- video `H.264`
- audio opcional no MVP
- segmentos curtos, por exemplo `2s`

## Mudancas Necessarias No `superWebRTCStream`

### Fase 1: API para o Roku

Adicionar um pequeno servidor HTTP com:

- `GET /api/windows`
- `GET /health`

O endpoint `/api/windows` deve listar as janelas publicadas e, quando houver stream ativo, expor `streamUrl`.

### Fase 2: Captura real da janela

Adicionar uma etapa de captura por janela usando uma tecnologia do Windows, por exemplo:

- `Windows Graphics Capture`
- alternativa futura: `Desktop Duplication`, se necessario

Saida esperada:

- frames da janela CEF associada ao `emei.lovable.app`

### Fase 3: Encoder e HLS

Transformar os frames em HLS:

- codificar em `H.264`
- gerar `index.m3u8`
- gerar segmentos `.ts`
- gravar segmentos numa pasta temporaria controlada pela aplicacao

### Fase 4: Servir os arquivos HLS

O mesmo servidor HTTP deve servir:

- playlist `.m3u8`
- segmentos `.ts`

### Fase 5: Controle de ciclo de vida

Cada janela publicada precisa ter:

- `slug`
- pasta temporaria do stream
- estado `Starting`, `Streaming`, `Error`, `Stopped`
- limpeza de segmentos antigos

## Mudancas Necessarias No `rokuweb`

O app Roku precisa deixar de ser so uma lista textual e passar a:

- buscar `/api/windows`
- escolher uma janela ou stream padrao
- criar um `ContentNode` com `streamFormat = "hls"`
- tocar `streamUrl` num `Video` node

## Requisitos Que Ja Temos

- maquina Windows hospedeira
- app CEF capaz de abrir `emei.lovable.app`
- app Roku customizado
- comunicacao HTTP basica dentro da rede local

## Requisitos Que Ainda Faltam

- API JSON real no `superWebRTCStream`
- pipeline de captura por janela
- encoder H.264
- geracao e hospedagem HLS
- player HLS no `rokuweb`
- tratamento de firewall e porta publica

## Caminho Mais Direto Para O MVP

1. Criar `/api/windows` no `superWebRTCStream`.
2. Fazer o `rokuweb` exibir e selecionar streams reais.
3. Publicar apenas uma janela fixa: `https://emei.lovable.app`.
4. Implementar HLS so para essa janela.
5. Depois generalizar para varias janelas.

## Decisao Tecnica Recomendada

Se o objetivo imediato for fazer a Roku mostrar `emei.lovable.app`, o melhor MVP nao e tentar "abrir o site no Roku". O melhor MVP e:

- abrir o site no CEF no Windows
- capturar essa janela
- converter para HLS
- fazer a Roku tocar esse HLS

Isso reduz incompatibilidades de navegador do Roku e coloca toda a compatibilidade web no lado Windows.

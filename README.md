# Roku Web Viewer

Aplicativo Roku para consumir dados e streams publicados pelo `super`.

## Estado Atual

Hoje o app:

- consulta `http://SERVIDOR_IP:PORTA/api/windows`
- mostra uma lista textual das janelas publicadas

Hoje o app ainda nao:

- reproduz video
- consome HLS
- abre `SEU_SITE_AQUI` diretamente

## Objetivo De Integracao

O fluxo desejado e:

1. o `super` abre `https://SEU_SITE_AQUI` no CEF
2. o `super` captura essa janela
3. o `super` publica um stream HLS
4. o `rokuweb` toca esse stream

## Endpoint Esperado

O app Roku depende de um servidor HTTP no Windows com este contrato:

`GET http://SERVIDOR_IP:PORTA/api/windows`

Resposta esperada:

```json
{
  "windowCount": 1,
  "windows": [
    {
      "id": "janela-principal",
      "title": "TITULO_EXEMPLO",
      "state": "Streaming",
      "initialUrl": "https://SEU_SITE_AQUI",
      "streamUrl": "http://SERVIDOR_IP:PORTA/streams/janela-principal/index.m3u8"
    }
  ]
}
```

## Empacotar

Para gerar o pacote `.zip`, basta dar dois cliques em:

- `Gerar-Pacote-Roku.cmd`
- compatibilidade: `Abrir-App.cmd`

Ou no PowerShell:

```powershell
.\Gerar-Pacote-Roku.ps1
```

Isso gera `hello-roku.zip`.

## Monorepo

Para compilar o monorepo inteiro de uma vez:

- `Compilar-Projetos.cmd`
- compatibilidade: `Compilar-Tudo.cmd`

## Como testar na TV

1. Abra o `super` no Windows.
2. Crie uma janela com `https://SEU_SITE_AQUI`.
3. Confirme que o servidor Windows responde em `http://SERVIDOR_IP:PORTA/api/windows`.
4. Gere o pacote com `Gerar-Pacote-Roku.cmd`.
5. Envie `hello-roku.zip` para a Roku em modo desenvolvedor.
6. Abra o canal na TV.

Resultado esperado:

- o app Roku conecta em `SERVIDOR_IP:PORTA`
- a TV mostra a lista de janelas publicadas pelo `super`
- o item da `SEU_SITE_AQUI` aparece na lista

Observacao:

- nesta etapa o teste e de integracao e descoberta
- a reproducao de video HLS ainda nao foi implementada

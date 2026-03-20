# Roku Web Viewer

Aplicativo Roku para consumir dados e streams publicados pelo `super`.

## Estado Atual

Hoje o app:

- consulta `http://10.1.0.10:8090/api/windows`
- mostra uma lista textual das janelas publicadas

Hoje o app ainda nao:

- reproduz video
- consome HLS
- abre `emei.lovable.app` diretamente

## Objetivo De Integracao

O fluxo desejado e:

1. o `super` abre `https://emei.lovable.app` no CEF
2. o `super` captura essa janela
3. o `super` publica um stream HLS
4. o `rokuweb` toca esse stream

## Endpoint Esperado

O app Roku depende de um servidor HTTP no Windows com este contrato:

`GET http://10.1.0.10:8090/api/windows`

Resposta esperada:

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

## Empacotar

Para gerar o pacote `.zip`, basta dar dois cliques em:

- `Abrir-App.cmd`

Ou no PowerShell:

```powershell
.\Abrir-App.ps1
```

Isso gera `hello-roku.zip`.

## Como testar na TV

1. Abra o `super` no Windows.
2. Crie uma janela com `https://emei.lovable.app`.
3. Confirme que o servidor Windows responde em `http://10.1.0.10:8090/api/windows`.
4. Gere o pacote com `Abrir-App.cmd`.
5. Envie `hello-roku.zip` para a Roku em modo desenvolvedor.
6. Abra o canal na TV.

Resultado esperado:

- o app Roku conecta em `10.1.0.10:8090`
- a TV mostra a lista de janelas publicadas pelo `super`
- o item da `emei.lovable.app` aparece na lista

Observacao:

- nesta etapa o teste e de integracao e descoberta
- a reproducao de video HLS ainda nao foi implementada

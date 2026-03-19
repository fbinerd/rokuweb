# Hello Roku

Base minima de aplicativo Roku usando BrightScript + SceneGraph.

## Estrutura

- `manifest`
- `source/main.brs`
- `components/HomeScene.xml`
- `components/HomeScene.brs`
- `package.ps1`

## Empacotar em zip

No PowerShell:

```powershell
.\package.ps1
```

Isso gera o arquivo `hello-roku.zip` com caminhos relativos compativeis com o sideload da Roku.

## Instalar na TV Roku

1. Ative o Developer Mode na Roku TV/dispositivo.
2. Descubra o IP da TV na rede local.
3. No navegador, abra `http://IP_DA_TV`.
4. Faça login com a senha de desenvolvedor configurada.
5. Envie o arquivo `hello-roku.zip`.

Ao abrir, o app mostra a mensagem `Hello`.

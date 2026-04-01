# Como definir o ícone do SuperPainel

1. Converta a imagem splash_screen_hd.png para o formato .ico (ícone do Windows).
   - Você pode usar um site como https://icoconvert.com/ ou um editor de imagens para converter.
   - Recomenda-se tamanho 256x256 ou múltiplos tamanhos (16x16, 32x32, 48x48, 256x256).

2. Coloque o arquivo .ico gerado na pasta do projeto (ex: super/src/WindowManager.App/).

3. No arquivo WindowManager.App.csproj, adicione:

```xml
<PropertyGroup>
  <ApplicationIcon>nome_do_arquivo.ico</ApplicationIcon>
</PropertyGroup>
```

4. No Visual Studio, clique com o botão direito no projeto > Propriedades > Aplicativo > Ícone e selecione o arquivo .ico.

5. Recompile o projeto. O executável gerado terá o novo ícone.

---

Se quiser, posso converter a imagem para .ico automaticamente se você preferir, ou guiar no processo manual.
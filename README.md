# Notch

Notch discreto para Windows 10/11: anel de bateria + hora; ao passar o mouse abre com a música do Spotify (capa, progresso, controles) e volume.

## Baixar
[download/Notch.exe](download/Notch.exe) → botão **Download raw file**. Não precisa instalar nada.
Se o SmartScreen avisar: *Mais informações* → *Executar assim mesmo*.

## Uso
- Abrir o `Notch.exe` mostra o notch e a tela de **Configurações** (posição Topo/Esquerda/Direita, iniciar com o Windows, avatar, sair). Abrir de novo com ele rodando só reabre as configurações.
- Mouse em cima do notch: abre com o avatar, a música e o volume. Botão direito: Configurações / Sair.
- O anel é a bateria; o raio no meio aparece quando está carregando.
- O avatar usa o WebView2 Runtime (já vem no Windows 10/11 atualizado). Sem ele, o notch funciona sem avatar.

## Compilar
O renderizador do avatar já está em `web/avatar.bundle.js`. Para gerar de novo: `cd web && npm install && npm run build`.

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o download
```

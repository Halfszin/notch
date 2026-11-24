# Notch

Notch discreto para Windows 10/11: anel de bateria + hora; ao passar o mouse abre com a música do Spotify (capa, progresso, controles) e volume.

## Baixar
[download/Notch.exe](download/Notch.exe) → botão **Download raw file**. Não precisa instalar nada.
Se o SmartScreen avisar: *Mais informações* → *Executar assim mesmo*.

## Uso
- Mouse em cima: abre.
- Botão direito: posição (Topo / Esquerda / Direita) ou Sair.

## Compilar
```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o download
```

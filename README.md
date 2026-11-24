# Orbi

Notch discreto para Windows 10/11 com avatar: anel de bateria (raio quando carrega) + hora; ao passar o mouse abre com o avatar, a música do Spotify (capa, progresso, controles) e o volume.

## Instalar
Baixe o **[OrbiSetup.exe](../../releases/latest/download/OrbiSetup.exe)** (página [Releases](../../releases)) e abra. Ele instala o Orbi no menu Iniciar, abre o app e já pode ser desinstalado em *Configurações do Windows → Apps*.
Se o SmartScreen avisar: *Mais informações* → *Executar assim mesmo*.

## Uso
- Abrir o **Orbi** mostra o notch e a tela de **Configurações** (posição Topo/Esquerda/Direita, iniciar com o Windows, avatar, sair). Abrir de novo com ele rodando só reabre as configurações.
- Mouse em cima do notch: abre. Botão direito: Configurações / Sair.
- Erros ficam em `%AppData%\Orbi\erros.log`.
- O avatar usa o WebView2 Runtime (já vem no Windows 10/11 atualizado). Sem ele, o notch funciona sem avatar.

## Como sai uma versão
Todo push no `main` roda `.github/workflows/build.yml` numa máquina Windows: compila, abre o Orbi, tira prints (branch `ci-results`) e, se ele continuar aberto sem erros, gera o instalador (`installer.iss`) e publica em Releases com a versão de `<Version>` do `Orbi.csproj`.

## Compilar localmente
```
dotnet publish Orbi.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```
O renderizador do avatar está em `web/avatar.bundle.js`. Para gerar de novo: `cd web && npm install && npm run build`.

# DaoStudio Desktop

Aplicativo desktop em **Avalonia 11.1.3** e **.NET 8** com design system minimalista obsidian (`#070709` / `#0C0C10` / acento esmeralda `#10B981`).

## Estrutura de Módulos

- **`00` Assistente Inicial (`TourView`)**: abertura animada sem sidebar visível durante a personalização; perguntas em etapas que conectam objetivo, universo/glossário e estilo ao workspace de destino.
- **`01` In-Game Bridge (`LauncherView`)**: detecção automática Steam para os 5 perfis em `profiles/`, patch transacional com backup/rollback e servidor loopback (`127.0.0.1`) em porta efêmera com telemetria L0/L1/L2. Em modo debug, a chave de licença é opcional.
- **`02` Studio CAT (`WorkbenchView`)**: editor de localização com trava de variáveis/tags, glossário canônico Aho-Corasick, filtros com indicadores de aprovação e persistência no cache L1/L0 via `Ctrl + Enter`.
- **`03` Novels & Mangás (`MaterialsView`)**: abas segmentadas para **Manhua** (limite de caracteres por balão + roteiro de typesetting), **Webnovel** (divisor de capítulos e exportação bilíngue ZH + PT-BR) e **Lore & Wiki**.

## Executar e Testar

```powershell
dotnet run --project src/DaoStudio.Desktop/DaoStudio.Desktop.csproj
dotnet test tests/DaoStudio.Desktop.Tests/DaoStudio.Desktop.Tests.csproj --no-restore -p:UseSharedCompilation=false
dotnet build src/DaoStudio.Desktop/DaoStudio.Desktop.csproj --no-restore -p:UseSharedCompilation=false
```

## Patch e Armazenamento Local

- Informe uma pasta de staging separada da instalação do jogo para testar `PatchTransactionManager` com backup e rollback automáticos (feche o jogo antes de aplicar ou restaurar).
- O banco SQLite L1 e a chave `cache.key` são armazenados localmente em `LocalApplicationData/DaoStudio` (ou no diretório injetado via DI nos testes).

## Empacotamento e Release

```powershell
# Pacote Release portátil (requer .NET 8 Desktop Runtime no Windows)
dotnet publish src/DaoStudio.Desktop/DaoStudio.Desktop.csproj -c Release --no-restore -p:UseSharedCompilation=false -o releases/DaoStudio-v0.1.0-win-x64

# Empacotamento com Velopack (requer CLI global 'vpk' e runtime pack win-x64)
./scripts/publish-velopack.ps1 -Version 0.1.0
```

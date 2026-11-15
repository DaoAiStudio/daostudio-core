# DaoStudio Core

Suíte desktop Open-Core de tradução e localização para jogos **Xianxia / Wuxia**, **Mangás / Manhua**, **Webnovels** e **Guias de Lore**, construída em **.NET 8** e **Avalonia UI 11.1.3** para o ecossistema [DaoStudio AI](https://daoaistudio-zeta.vercel.app/).

---

## Visão Geral da Arquitetura

O repositório é estruturado em camadas modulares e desacopladas:

| Projeto | Caminho | Responsabilidade |
| --- | --- | --- |
| **Contracts** | [src/DaoStudio.Contracts](src/DaoStudio.Contracts) | Contratos compartilhados (`TranslationContracts`, `GlossaryAndStudioContracts`, `ManifestContracts`) e DTOs para bridge/plugins. |
| **Engine** | [src/DaoStudio.Engine](src/DaoStudio.Engine) | Cache em 2 níveis (`L0MemoryCache` + `L1SqliteCache`), `TagMasker`, `GlossaryMatcher` (Aho-Corasick / Trie), `StringNormalizer` e parsers `.json` / `.csv` / `.txt`. |
| **Bridge** | [src/DaoStudio.Bridge](src/DaoStudio.Bridge) | Servidor HTTP/WebSocket loopback autenticável (`BridgeServer`), `MicroBatcher`, `BridgeTranslationOrchestrator` (com circuit breaker) e `PostTranslationValidator`. |
| **Patcher** | [src/DaoStudio.Patcher](src/DaoStudio.Patcher) | Descoberta automática da Steam no Windows (`VdfParser`, `WindowsSteamLocator`), validação SHA-256 (`ManifestDownloader`) e transações atômicas com rollback e proteção ZipSlip (`PatchTransactionManager`). |
| **UnityPlugin** | [src/Plugins/DaoStudio.UnityPlugin](src/Plugins/DaoStudio.UnityPlugin) | Filtro de texto CJK/contexto (`TextInterceptorFilter`) e cliente loopback (`BridgeLoopbackClient`) para jogos Unity. |
| **Desktop** | [src/DaoStudio.Desktop](src/DaoStudio.Desktop) | Aplicativo Avalonia com tema obsidian minimalista e 4 experiências integradas. |

---

## Workspaces no Aplicativo Desktop

- **`00` Assistente Inicial (Tour)** — Fluxo guiado de personalização em tela cheia (com o menu lateral recolhido durante a configuração) e animação de abertura que constrói o pipeline em 4 nós passo a passo (`Objetivo` → `Universo` → `Preferências` → `Workspace`), com opção de pular diretamente para o workspace.
- **`01` In-Game Bridge** — Detecção automática da instalação na Steam, aplicação/restauração transacional de patches e controle da bridge loopback (`127.0.0.1`) com telemetria em tempo real de acertos `L0 RAM` / `L1 SQLite` / `L2 Cloud` (modo debug ativo sem exigência de licença).
- **`02` Studio CAT** — Editor de segmentos lado a lado com busca e filtros (`Todos`, `Pendentes`, `Aprovados`), inserção rápida de placeholders obrigatórios (`{PlayerName}`, `{0}`, tags `<color>`), validação contra termos proibidos e salvamento no cache L0/L1 via `Ctrl + Enter`.
- **`03` Novels & Mangás** — Workspace segmentado para **`Manhua`** (orçamento de caracteres por balão + roteiro para typesetting), **`Webnovel`** (divisor de capítulos em chinês + exportação bilíngue ZH / PT-BR) e **`Lore & Wiki`**, compartilhando o glossário canônico e cache L1 com o modo In-Game.

---

## Catálogo Multi-Jogo Suportado (`profiles/`)

| Game ID | Título | Steam AppID | Engine | Loader |
| --- | --- | --- | --- | --- |
| `guigubahuang` | Tale of Immortal (鬼谷八荒) | `1468810` | `unity-il2cpp` | `melonloader` |
| `herosadventure` | Hero's Adventure: Road to Passion (大侠立志传) | `1948980` | `unity-il2cpp` | `bepinex` |
| `michangsheng` | Mi ChangSheng (觅长生) | `1189490` | `unity-mono` | `bepinex` |
| `wanderingsword` | Wandering Sword (逸剑风云决) | `1876890` | `unity-mono` | `bepinex` |
| `xiuxiansimulator` | Amazing Cultivation Simulator (了不起的修仙模拟器) | `955900` | `unity-mono` | `bepinex` |

Cada perfil acompanha glossário em `data/glossary/` e amostras de treino em `data/seed/`, além de blocos de exemplo em `data/materials/xianxia-materials-samples.json`.

---

## Compilação, Testes e Empacotamento

```powershell
# Compilar toda a solução
dotnet build DaoStudio.sln -p:UseSharedCompilation=false

# Executar toda a suíte de testes (159 testes: Patcher, Engine, Desktop, Bridge)
dotnet test DaoStudio.sln --no-build -p:UseSharedCompilation=false

# Executar o aplicativo Desktop em desenvolvimento
dotnet run --project src/DaoStudio.Desktop/DaoStudio.Desktop.csproj

# Publicar distribuição Windows (Release)
dotnet publish src/DaoStudio.Desktop/DaoStudio.Desktop.csproj -c Release -p:UseSharedCompilation=false -o releases/DaoStudio-v0.1.0-win-x64
```

Para gerar pacotes instaladores com **Velopack**, consulte [scripts/publish-velopack.ps1](scripts/publish-velopack.ps1) e [src/DaoStudio.Desktop/README.md](src/DaoStudio.Desktop/README.md).

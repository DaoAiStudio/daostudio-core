# Estudo de caso: localização de jogos em tempo real com núcleo reutilizável

Portfólio: `realtime-game-llm-architecture` · Projeto: `daostudio-core` · Raia 5

## Problema e recorte

Traduzir cada atualização de texto de um jogo por uma chamada a um LLM introduz espera de rede, custo por repetição e resultados inconsistentes. Um menu reaparece muitas vezes; uma fala muda apenas o nome do jogador ou a quantidade de recursos. Tags de rich text e variáveis não podem ser traduzidas como prosa: perder `{0}`, alterar uma cor ou duplicar um marcador pode corromper a interface. Mesmo uma tradução correta pode exceder o espaço disponível ou usar glifos ausentes no atlas SDF do TextMeshPro.

Este recorte entrega curadoria e QA reproduzíveis para Tale of Immortal (鬼谷八荒): 64 entradas de glossário e 51 segmentos aprovados de menus, criação de personagem, cultivo, técnicas, itens, seitas e combate. Os 16 termos e cinco segmentos iniciais foram preservados. Os novos exemplos são conteúdo de teste autoral representativo; não são uma extração nem uma tradução oficial dos diálogos do jogo. As traduções são convenções deste projeto. `approved: true` expressa aprovação do fixture, sem sugerir homologação pelo estúdio do jogo.

## Dual-Mode e fronteira Open-Core

`DaoStudio.Engine` concentra o núcleo reutilizável: mascaramento, normalização, hash, glossário, parsers e caches. Os contratos ficam em `DaoStudio.Contracts`, separados da integração com Unity e do transporte HTTP.

| Consumidor | Uso do núcleo | Estado observado |
| --- | --- | --- |
| Player Runtime | Interceptar texto, consultar templates, recorrer ao Bridge e restaurar tags | Existem plugin e orquestrador; esta execução não valida o jogo real |
| Studio Workbench | Importar segmentos, revisar terminologia, aprovar e produzir seed | Parsers e DTOs disponíveis; `DaoStudio.Desktop/Program.cs` ainda tem `Main` vazio |

A proposta Open-Core é tornar curadoria, validação e cache local reutilizáveis, mantendo uma fronteira explícita para serviços remotos de tradução. Isso é uma delimitação arquitetural, não uma afirmação sobre licença comercial, serviço publicado ou produto desktop concluído.

## Canonicalização determinística e integridade

O fluxo local é `Mask → Normalize → SHA-256 → CacheKey → lookup → validar → Unmask`. A chave composta inclui jogo, idioma de destino, hash do template e versão do glossário, evitando misturar traduções de contextos incompatíveis.

O `TagMasker` usa `RegexOptions.NonBacktracking`, com contadores independentes para `VAR`, `TAG` e `NUM`. A mesma estrutura produz marcadores determinísticos; variáveis repetidas recebem tokens distintos. O algoritmo não depende de tentativas exponenciais de backtracking. A suíte existente também exercita uma entrada malformada com 400 mil caracteres e limite de cinco segundos. Isso verifica um caso adverso, sem equivaler a uma prova de desempenho para qualquer entrada.

`ValidateMaskIntegrity` exige exatamente o mesmo conjunto e multiplicidade de tokens. `Unmask` faz uma única passagem, impedindo que texto restaurado com aparência de token seja expandido novamente. A normalização usa NFC, regulariza espaços e finais de linha; o mascaramento anterior preserva as quebras originais nos bindings.

Nos testes do seed, tanto fonte quanto tradução são mascaradas. Além da validação de tokens, os mapas são comparados para detectar mudanças nos valores originais, como `{0}` trocado por `{1}` ou alteração de cor. A tradução restaurada precisa ser idêntica ao PT-BR do fixture. A remoção deliberada de um token deve ser rejeitada. Há exemplos com quebra real de linha e com a sequência literal `\n`.

Nomes e quantidades do benchmark são bindings de `{PlayerName}` e `{0}` aplicados depois do lookup. O cache guarda templates, nunca a fala já renderizada para outro jogador. O `TagMasker` não infere nomes próprios em texto livre já interpolado. Seu modo opcional de números isolados também não substitui um contrato de bindings. A simulação não demonstra deduplicação automática de nomes arbitrários recebidos do jogo.

## Cache L0/L1/L2 e controle de chamadas

- **L0:** memória com capacidade limitada e remoção FIFO. Não é LRU. O caminho aquecido evita SQLite e rede.
- **L1:** SQLite em WAL, comandos preparados e chave composta. O payload usa `AesGcm`, chave de 32 bytes, nonce de 12 bytes, tag de autenticação de 16 bytes e a chave de cache como dados associados. Metadados das chaves continuam no banco; não se trata de criptografia integral do arquivo SQLite.
- **L2:** fronteira remota identificada pelo campo `FromCache` da resposta do serviço. O orquestrador registra esse resultado como L2. O `TieredTranslationCache` local implementa apenas L0/L1; esta suíte não executa nem mede um serviço L2.

O cache hierárquico grava em L1 antes de L0 e promove um hit persistido para memória. Sua exclusão mútua impede promoção concorrente de valor obsoleto. A chave padrão de criptografia é da sessão do processo; reabrir o banco entre processos exige fornecer e proteger uma chave estável. O benchmark fornece uma chave explícita e reabre a conexão para verificar persistência.

No Bridge, `MicroBatcher` agrupa chamadas em janela padrão de 65 ms, com até 25 itens por lote e single-flight por `CacheKey`. Chamadores da mesma tradução compartilham a operação pendente. Lotes são separados por jogo, idioma e versão do glossário. Cancelamento de um chamador não deve cancelar os demais interessados; falhas de lote completam a espera com resultado ausente.

O Circuit Breaker está no `CloudTranslationClient`, não no Engine: estados Closed/Open/HalfOpen, limiar padrão de três falhas, cooldown de 15 segundos, timeout de requisição e retries limitados. O orquestrador devolve o original quando não obtém tradução válida. Esses pontos foram inspecionados no código; não são resultados da suíte Engine executada nesta entrega.

## Fontes, segurança e compliance

O perfil declara `NotoSansCJK_SC_SDF` com licença `SIL-OFL-1.1` (SIL OFL 1.1). A distribuição deve carregar os avisos e a licença aplicáveis ao artefato efetivamente usado. O nome e a licença no JSON não comprovam procedência, cobertura de glifos ou conformidade de um bundle. Para PT-BR, a validação visual deve incluir acentos, cedilha, tamanhos, fallback de fontes e recorte de texto. Nenhum teste desta suíte renderiza fontes SDF.

`ManifestDownloader` verifica SHA-256 do download antes de publicar o arquivo. Um checksum detecta divergência em relação ao manifesto; não autentica sozinho a origem do manifesto. Os hashes de loader e fonte em `profiles/guigubahuang.json` ainda são zeros e precisam ser substituídos por valores verificados antes de distribuição. O perfil não foi modificado por esta raia.

A extração ZIP faz preflight de todas as entradas, resolve caminhos dentro do destino e rejeita links simbólicos: defesa contra Zip Slip e escrita fora da pasta esperada. O gerenciamento de patch prepara backups e metadados antes de modificar arquivos do jogo.

**Limite da expressão “rollback atômico”:** o código publica o diretório de backup completo por `Directory.Move` antes de aplicar o patch. Em falha de aplicação, chama rollback; depois de iniciada a restauração, ignora cancelamento para tentar terminá-la. Entretanto, aplicação e restauração copiam arquivos individualmente. Não há transação atômica global nem garantia demonstrada contra queda de energia no meio da restauração. Esta entrega documenta essa diferença, sem prometer uma propriedade que o código não implementa.

## Curadoria e critérios de QA

O glossário contém os dez reinos, subestágios iniciais, formas de Fundação, cargos, atributos, técnicas e UI. Cada registro contém `zh`, `pinyin`, `ptBr`, `en`, `es`, `category`, `forbiddenLiterals` e `maxChars`. Listas de literais proibidos vazias nas novas entradas não são listas exaustivas de traduções inadequadas; os testes também exigem a presença da tradução canônica de cada termo encontrado na fonte.

O matcher faz busca por substring. Não foi adicionado um termo genérico que também estivesse contido em `炼气期`: o teste preexistente continua exigindo exatamente um match. Termos como `道` podem ser encontrados dentro de termos compostos; o seed mantém a convenção correspondente. A única flexão explicitamente admitida no teste é `Pedras Espirituais`, já presente no segmento inicial, para `Pedra Espiritual`.

Os testes verificam ids e chaves únicos, conteúdo não vazio, aprovação, os cinco contextos permitidos e ausência de literais proibidos tanto pelo matcher quanto por inspeção global da lista. `maxChars` não é usado como orçamento de uma fala inteira: o matcher atual escolhe o menor limite dos termos encontrados, o que pode sinalizar frases legítimas. Garantir encaixe em UI exige orçamento por segmento e medição visual; não é uma conclusão deste benchmark.

## Experimento e resultados reais

Execução local em Windows, em 15/11/2026: SDK .NET `10.0.301`, alvo `net8.0`, runtime reportado `.NET 8.0.33`, xUnit Adapter `2.8.2`, build Debug. Não houve chamadas ao LLM ou execução do jogo.

O teste grava os 51 templates usando `TieredTranslationCache`, fecha L1, reabre com a mesma chave e verifica WAL e a promoção L1 → L0 de cada entrada. Faz 300 consultas de aquecimento fora da medição. Em seguida executa exatamente 100 consultas em cinco blocos de 17 hits e três misses. Todos os segmentos do seed são consultados; as consultas adicionais exercitam nomes e quantidades variáveis. Os misses têm textos novos e não são inseridos no cache durante a simulação.

A temporização usa `Stopwatch.GetTimestamp` em torno de `TryGetAsync`, acumulando somente as 85 consultas com hit. Exclui criação da chave, mascaramento, renderização, aquecimento, escrita, misses e rede. É uma média de lookup local, não latência ponta a ponta, percentil ou SLA.

| Verificação | Resultado observado |
| --- | --- |
| Suíte completa `DaoStudio.Engine.Tests` | 70 aprovados, 0 falhas, 0 ignorados |
| Novos testes de lore/seed/benchmark | 3 aprovados, incluídos nos 70 |
| Glossário | 64 entradas completas e únicas |
| Seed | 51 segmentos aprovados |
| ForbiddenHits | 0 em todos os segmentos |
| Integridade estrutural e restauração | 100% dos 51 segmentos |
| Gameplay inicial simulado | 85 hits / 100 consultas = 85% (> 80%) |
| Média aquecida L0 via cache hierárquico | 0,001694 ms (< 1 ms) |
| Bindings distintos usados | 39 nomes e 46 quantidades |
| Teste preexistente de L1 aquecido | 0,015096 ms, média de 3.000 hits |
| Duração total reportada pelo runner | 1,1967 s, sem incluir restore/build |

Os demais projetos de teste não foram executados nesta raia. Os testes existentes de Engine cobrem, entre outros pontos, persistência, concorrência WAL, chave incorreta, ausência do payload em texto claro, normalização e parsers. Não se atribuem a eles validações de HTTP, patcher, desktop ou integração visual.

A proporção 85/15 é uma carga sintética definida pelo teste: prova que o seed atende esse cenário, não estima a distribuição real de falas do jogo. Não há medição monetária; o resultado mostra consultas que poderiam evitar tradução remota. Medidas locais variam com máquina, concorrência e configuração de build.

## Reprodução no sandbox Windows

Não criar `NuGet.Config` no repositório. Nesta execução, o primeiro restore recebeu acesso negado ao arquivo NuGet do perfil. O segundo funcionou com `APPDATA` temporário, sem alterar a configuração pessoal:

```powershell
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1'
'<configuration><packageSources><clear /></packageSources></configuration>' |
    Set-Content "$env:TEMP\nuget-offline.config" -Encoding UTF8
$env:APPDATA = Join-Path $env:TEMP 'daostudio-nuget-appdata'
New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null
dotnet restore tests/DaoStudio.Engine.Tests/DaoStudio.Engine.Tests.csproj --configfile "$env:TEMP\nuget-offline.config" --source "$env:USERPROFILE\.nuget\packages"
dotnet test tests/DaoStudio.Engine.Tests/DaoStudio.Engine.Tests.csproj --no-restore -p:UseSharedCompilation=false --logger 'console;verbosity=detailed'
```

Executar em um processo PowerShell dedicado mantém a alteração de ambiente restrita à reprodução. Os pacotes precisam estar disponíveis no cache local. O logger detalhado publica as métricas pelos testes; bancos temporários são removidos após a execução. Nenhum código de produção ou teste preexistente foi editado.

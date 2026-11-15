# DaoStudio Desktop

Avalonia 11.1.3, .NET 8, Player Launcher e Studio Workbench. Tema Zinc/Emerald.

## Executar

`dotnet run --project src/DaoStudio.Desktop/DaoStudio.Desktop.csproj`

A detecção Steam ocorre ao abrir a janela. O Launcher valida apenas o formato da licença;
sem contrato de autenticação/endpoint configurado, a sessão é explicitamente offline.
A Bridge atende em loopback com porta efêmera, cache L0/L1 e fallback para o original.
O painel distingue servidor escutando de cliente conectado (a Bridge não expõe contagem de clientes).
O token exibido é resumido, sem expor a credencial completa no diagnóstico.

## Patch

Informe uma pasta de staging separada da instalação do jogo, com a árvore de arquivos
pronta para copiar (loader/plugin/fontes preparados). O perfil atual tem hashes SHA-256
zerados; não há download automático de artefatos de produção nem pacote de patch incluído.
O gerenciador real preserva backup e realiza rollback. Feche o jogo antes da instalação/restauração.

## Revisão

Importe/exporte usando o caminho de arquivo na tela. Ctrl+Enter aprova, grava L1/L0 e
avança. Variáveis e tags devem preservar identidade e multiplicidade. Termos proibidos,
limites do glossário e texto vazio bloqueiam aprovação. Alterar texto revoga a aprovação.
JSON/CSV preservam os campos de revisão. TXT acompanha um `.review.json`: mantenha ambos
para reimportar originais e aprovações. Um acompanhante incompatível com o TXT é ignorado.
Amostras são opcionais e só carregam mediante comando.

O SQLite e `cache.key` ficam em LocalApplicationData/DaoStudio, ou no diretório passado ao DI.
A chave persistente permite reabrir o cache; depende das permissões locais do diretório,
não de um cofre de credenciais. Não distribua esse diretório como conteúdo do aplicativo.

## Verificação e pacote

Testes xUnit não abrem janela. Incluem HTTP real em porta efêmera e mocks sem socket.
No sandbox, use o config NuGet temporário definido na tarefa. Caso o SDK tente ler o
NuGet.Config do usuário sem permissão, defina APPDATA apenas no processo para uma pasta
em TEMP antes do restore/build/test. Nenhum NuGet.Config é necessário no repositório.

`dotnet test tests/DaoStudio.Desktop.Tests/DaoStudio.Desktop.Tests.csproj --no-restore -p:UseSharedCompilation=false`

`dotnet build src/DaoStudio.Desktop/DaoStudio.Desktop.csproj --no-restore -p:UseSharedCompilation=false`

`./scripts/publish-velopack.ps1 -Version 1.0.0` publica self-contained win-x64 e executa
`vpk pack` em releases. Requer CLI vpk e runtime packs instalados. Não publica releases
remotos, não assina instaladores e não configura um feed de atualização.

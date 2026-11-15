# Testes isolados da Raia 3

No sandbox Windows, execute a partir da raiz do worktree:

```powershell
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1'
$env:UseArtifactsOutput='true'
$env:ArtifactsPath=(Join-Path $PWD 'tests/DaoStudio.Patcher.Tests/artifacts')
$env:APPDATA=(Join-Path $PWD 'tests/DaoStudio.Patcher.Tests/artifacts/appdata')
dotnet restore tests/DaoStudio.Patcher.Tests/DaoStudio.Patcher.Tests.csproj --source "$env:USERPROFILE\.nuget\packages" --configfile tests/DaoStudio.Patcher.Tests/NuGet.Config
dotnet test tests/DaoStudio.Patcher.Tests/DaoStudio.Patcher.Tests.csproj --no-restore -p:UseSharedCompilation=false
```

A configuração NuGet local evita fontes externas. APPDATA isolado evita a leitura do NuGet.Config do perfil bloqueado pelo sandbox. UseArtifactsOutput mantém inclusive os artefatos da referência Contracts no diretório permitido de testes. Essas variáveis valem apenas para o processo de shell atual.

O plugin tem target net6.0; os testes rodam em net8.0. HttpClient é injetado e sua vida útil pertence ao chamador. A sessão é lida na construção do cliente; uma nova sessão deve criar um novo cliente para descartar o cache L0. Não há persistência de traduções.

O patch exige diretórios de jogo e staging separados e rejeita reparse points. O backup completo é publicado por renomeação antes da primeira alteração. Uma aplicação com backup existente é recusada; use RollbackAsync antes de reaplicar. A restauração usa um manifesto persistido, incluindo diretórios criados, e pode ser feita por uma nova instância. Execute patch/rollback com o jogo fechado, sem outro processo alterando a árvore durante a operação.

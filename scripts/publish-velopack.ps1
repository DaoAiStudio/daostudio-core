param(
    [string]$Version = "1.0.0",
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "../releases")
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../src/DaoStudio.Desktop/DaoStudio.Desktop.csproj'
$publish = Join-Path $OutputDirectory 'publish/win-x64'
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw 'Velopack CLI ausente. Instale com: dotnet tool install --global vpk'
}
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou.' }
vpk pack --packId DaoStudio --packVersion $Version --packDir $publish --mainExe DaoStudio.Desktop.exe --outputDir $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'vpk pack falhou.' }

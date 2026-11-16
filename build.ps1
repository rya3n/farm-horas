param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'Release'))

$ErrorActionPreference = 'Stop'

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$steamworks = Join-Path $PSScriptRoot 'Dependencies\steam_api64.dll'
$manifest = Join-Path $PSScriptRoot 'src\FarmHoras.manifest'
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$executable = Join-Path $outputDirectory 'FarmHoras.exe'

if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'Instale o .NET Framework 4.8 em um Windows de 64 bits.'
}

if (!(Test-Path -LiteralPath $steamworks -PathType Leaf)) {
    throw 'Coloque steam_api64.dll na pasta Dependencies.'
}

New-Item -Path $outputDirectory -ItemType Directory -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$compileArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
    "/win32manifest:$manifest", "/out:$executable", '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll'
) + $sources
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) {
    throw "A compilacao falhou (codigo $LASTEXITCODE)."
}

Copy-Item -LiteralPath $steamworks -Destination $outputDirectory -Force
Write-Output "Compilado em $executable"

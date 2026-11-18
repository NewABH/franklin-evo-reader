param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'tools\dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $sourceArgs = @()
    if (Test-Path 'tools\nuget-feed\microsoft.net.illink.tasks.10.0.12.nupkg') {
        $sourceArgs = @('--source', (Join-Path $PSScriptRoot 'tools\nuget-feed'))
    }
    & dotnet run --project tests\ReaderTests.csproj -c Release -p:PublishAot=false -- -
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    $output = if ($SelfContained) { 'dist-self-contained' } else { 'dist' }
    $publishArgs = @('publish', 'FranklinEvoReader.csproj', '-c', 'Release', '-r', 'win-x64', '-o', $output, '-p:NuGetAudit=false')
    if (!$SelfContained -and (Test-Path 'tools\native\VC\Tools\MSVC')) {
        $msvc = Get-ChildItem 'tools\native\VC\Tools\MSVC' -Directory | Sort-Object Name -Descending | Select-Object -First 1
        $env:PATH = (Join-Path $msvc.FullName 'bin\Hostx64\x64') + ';' + $env:PATH
        $env:LIB = (Join-Path $msvc.FullName 'lib\x64') + ';' + (Join-Path $PSScriptRoot 'tools\native\SDK\um\x64') + ';' + (Join-Path $PSScriptRoot 'tools\native\SDK\ucrt\x64')
        $publishArgs += '-p:IlcUseEnvironmentalTools=true'
    }
    if ($SelfContained) {
        $publishArgs += @('-p:PublishAot=false', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
    }
    & dotnet @publishArgs @sourceArgs
    if ($LASTEXITCODE -ne 0) { throw 'EXE publish failed' }
    Copy-Item -LiteralPath README.md -Destination (Join-Path $output 'README.md') -Force
    & dotnet run --project tests\ReaderTests.csproj -c Release -p:PublishAot=false -- - (Join-Path $output 'FranklinEvoReader.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Published EXE verification failed' }
    Get-Item -LiteralPath (Join-Path $output 'FranklinEvoReader.exe')
    Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $output 'FranklinEvoReader.exe')
} finally { Pop-Location }

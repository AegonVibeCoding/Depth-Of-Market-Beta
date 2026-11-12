param([string]$OutputDirectory = "$PSScriptRoot\..\artifacts\depth-of-market-beta-build")
$ErrorActionPreference = 'Stop'
foreach ($platform in @('ATAS', 'ATASX')) {
    $sdk = if ($platform -eq 'ATAS') { 'C:\Program Files (x86)\ATAS Platform' } else { 'C:\Program Files\ATAS X' }
    $output = [IO.Path]::GetFullPath((Join-Path $OutputDirectory $platform))
    # Installed ATAS X 8.0.15 still uses Filter; ATAS beta 8.0.16 uses VolumeFilter.
    $legacyFilter = if ($platform -eq 'ATASX') { 'true' } else { 'false' }
    dotnet build "$PSScriptRoot\DepthOfMarketBeta.csproj" -c Release --nologo "-p:AtasDir=$sdk" "-p:LegacyDomFilter=$legacyFilter" "-p:OutputPath=$output/" "-p:IntermediateOutputPath=obj/$platform/Release/net10.0-windows/"
    if ($LASTEXITCODE -ne 0) { throw "$platform build failed" }
}

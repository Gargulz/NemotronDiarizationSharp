param(
  [Parameter(Mandatory = $true)]
  [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-Valid([bool] $condition, [string] $message) {
  if (-not $condition) { throw "Package validation failed: $message" }
}

$version = '0.1.0-preview.1'
$id = 'NemotronDiarizationSharp'
$nupkg = Join-Path $PackageDirectory "$id.$version.nupkg"
$snupkg = Join-Path $PackageDirectory "$id.$version.snupkg"
Assert-Valid (Test-Path -LiteralPath $nupkg -PathType Leaf) "Missing $id.$version.nupkg."
Assert-Valid (Test-Path -LiteralPath $snupkg -PathType Leaf) "Missing $id.$version.snupkg."

$packageRoot = Join-Path $env:RUNNER_TEMP 'nemotron-nupkg-contents'
$symbolsRoot = Join-Path $env:RUNNER_TEMP 'nemotron-snupkg-contents'
Remove-Item -LiteralPath $packageRoot, $symbolsRoot -Recurse -Force -ErrorAction SilentlyContinue
[IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $packageRoot)
[IO.Compression.ZipFile]::ExtractToDirectory($snupkg, $symbolsRoot)

$packageArchive = [IO.Compression.ZipFile]::OpenRead($nupkg)
try { $packageEntries = @($packageArchive.Entries | ForEach-Object FullName) }
finally { $packageArchive.Dispose() }
$expectedPackageEntries = @(
  '_rels/.rels',
  '[Content_Types].xml',
  "$id.nuspec",
  'DISTRIBUTION_STATUS.md',
  'LICENSE',
  'README.md',
  "lib/net10.0/$id.dll"
)
$unexpectedPackageEntries = @($packageEntries | Where-Object {
  ($_ -notin $expectedPackageEntries) -and ($_ -notmatch '^package/services/metadata/core-properties/[^/]+\.psmdcp$')
})
$missingPackageEntries = @($expectedPackageEntries | Where-Object { $_ -notin $packageEntries })
Assert-Valid ($unexpectedPackageEntries.Count -eq 0) "Unexpected .nupkg entries: $($unexpectedPackageEntries -join ', ')"
Assert-Valid ($missingPackageEntries.Count -eq 0) "Missing .nupkg entries: $($missingPackageEntries -join ', ')"

$symbolsArchive = [IO.Compression.ZipFile]::OpenRead($snupkg)
try { $symbolsEntries = @($symbolsArchive.Entries | ForEach-Object FullName) }
finally { $symbolsArchive.Dispose() }
$expectedSymbolsEntries = @('_rels/.rels', '[Content_Types].xml', "$id.nuspec", "lib/net10.0/$id.pdb")
$unexpectedSymbolsEntries = @($symbolsEntries | Where-Object {
  ($_ -notin $expectedSymbolsEntries) -and ($_ -notmatch '^package/services/metadata/core-properties/[^/]+\.psmdcp$')
})
$missingSymbolsEntries = @($expectedSymbolsEntries | Where-Object { $_ -notin $symbolsEntries })
Assert-Valid ($unexpectedSymbolsEntries.Count -eq 0) "Unexpected .snupkg entries: $($unexpectedSymbolsEntries -join ', ')"
Assert-Valid ($missingSymbolsEntries.Count -eq 0) "Missing .snupkg entries: $($missingSymbolsEntries -join ', ')"

$forbiddenAssets = @($packageEntries | Where-Object {
  $_ -match '(?i)(^|/)runtimes/' -or
  $_ -match '(?i)(^|/)models?/' -or
  $_ -match '(?i)\.(so|dylib|a|lib|exe|gguf|onnx|safetensors|pt|pth)$'
})
Assert-Valid ($forbiddenAssets.Count -eq 0) "Native or model assets found: $($forbiddenAssets -join ', ')"
$dllEntries = @($packageEntries | Where-Object { $_ -match '(?i)\.dll$' })
Assert-Valid (($dllEntries.Count -eq 1) -and ($dllEntries[0] -eq "lib/net10.0/$id.dll")) 'The package must contain only the managed SDK assembly DLL.'
$null = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $packageRoot "lib/net10.0/$id.dll"))

$nuspecPath = Join-Path $packageRoot "$id.nuspec"
[xml] $nuspec = Get-Content -LiteralPath $nuspecPath -Raw
$ns = [Xml.XmlNamespaceManager]::new($nuspec.NameTable)
$ns.AddNamespace('n', 'http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd')
$metadata = $nuspec.SelectSingleNode('/n:package/n:metadata', $ns)
$license = $metadata.SelectSingleNode('n:license', $ns)
$repository = $metadata.SelectSingleNode('n:repository', $ns)
$readme = $metadata.SelectSingleNode('n:readme', $ns)
$dependencies = @($metadata.SelectNodes('n:dependencies//n:dependency', $ns))
Assert-Valid ($metadata.SelectSingleNode('n:id', $ns).InnerText -eq $id) 'Unexpected package ID.'
Assert-Valid ($metadata.SelectSingleNode('n:version', $ns).InnerText -eq $version) 'Unexpected package version.'
Assert-Valid ($metadata.SelectSingleNode('n:authors', $ns).InnerText -eq 'Gargulz') 'Unexpected package author.'
Assert-Valid (($license.GetAttribute('type') -eq 'expression') -and ($license.InnerText -eq 'MIT')) 'MIT license expression missing.'
Assert-Valid ($readme.InnerText -eq 'README.md') 'NuGet README metadata is missing.'
Assert-Valid ($repository.GetAttribute('url') -eq 'https://github.com/Gargulz/NemotronDiarizationSharp') 'Unexpected repository URL.'
Assert-Valid ($repository.GetAttribute('commit') -eq $env:EXPECTED_COMMIT) 'Repository commit metadata does not match the checked-out commit.'
Assert-Valid ($dependencies.Count -eq 0) 'The managed package must not have runtime package dependencies.'

$symbolsNuspecPath = Join-Path $symbolsRoot "$id.nuspec"
[xml] $symbolsNuspec = Get-Content -LiteralPath $symbolsNuspecPath -Raw
$symbolsNs = [Xml.XmlNamespaceManager]::new($symbolsNuspec.NameTable)
$symbolsNs.AddNamespace('n', 'http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd')
$symbolsMetadata = $symbolsNuspec.SelectSingleNode('/n:package/n:metadata', $symbolsNs)
$symbolsRepository = $symbolsMetadata.SelectSingleNode('n:repository', $symbolsNs)
Assert-Valid ($symbolsMetadata.SelectSingleNode('n:id', $symbolsNs).InnerText -eq $id) 'Unexpected symbols package ID.'
Assert-Valid ($symbolsMetadata.SelectSingleNode('n:version', $symbolsNs).InnerText -eq $version) 'Unexpected symbols package version.'
Assert-Valid ($symbolsRepository.GetAttribute('commit') -eq $env:EXPECTED_COMMIT) 'Symbols repository commit metadata does not match the checked-out commit.'

$pdbPath = Join-Path $symbolsRoot "lib/net10.0/$id.pdb"
$pdbText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($pdbPath))
$sourceLinkPrefix = "raw.githubusercontent.com/Gargulz/NemotronDiarizationSharp/$($env:EXPECTED_COMMIT)/"
Assert-Valid ($pdbText.Contains($sourceLinkPrefix)) 'Source Link in the portable PDB does not map to the checked-out commit.'

$workspaceSlash = $env:GITHUB_WORKSPACE.Replace('\', '/')
foreach ($root in @($packageRoot, $symbolsRoot)) {
  foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
    $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($file.FullName))
    Assert-Valid ((-not $text.Contains($env:GITHUB_WORKSPACE)) -and (-not $text.Contains($workspaceSlash))) "Private workspace path found in '$($file.Name)'."
    Assert-Valid ($text -notmatch '(?i)([A-Z]:\\a\\|/home/runner/work/|/Users/runner/work/)') "Absolute runner build path found in '$($file.Name)'."
  }
}

Write-Output "PASS: $id $version metadata, empty dependency graph, Source Link commit $env:EXPECTED_COMMIT, managed-only archive contents, and no private build paths."

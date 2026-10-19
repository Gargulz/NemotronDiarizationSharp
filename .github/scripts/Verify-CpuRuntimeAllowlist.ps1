[CmdletBinding()]
param(
    [ValidateSet('Metadata', 'FullLocal')]
    [string] $Mode = 'Metadata',
    [string] $ProjectPath,
    [string] $ManifestPath,
    [string] $NativeDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$allowlist = @(
    'nemo_speech_asr_c.dll',
    'nemo_speech_asr.dll',
    'ggml.dll',
    'ggml-base.dll',
    'ggml-cpu.dll'
)
$originalInventory = @(
    'concrt140.dll', 'ggml-base.dll', 'ggml-cpu.dll', 'ggml.dll', 'llama.dll',
    'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll',
    'msvcp140_codecvt_ids.dll', 'nemo_speech_asr.dll', 'nemo_speech_asr_c.dll',
    'nemo_speech_nmt.dll', 'nemo_speech_nmt_c.dll', 'nemo_speech_tts.dll',
    'vcomp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll'
)
$expectedPackagePath = 'runtimes\nemotron\cpu\win-x64\native'

function Assert-Allowlist([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "CPU runtime allowlist validation failed: $Message" }
}

function Normalize-Path([string] $Value) {
    return $Value.Replace('/', '\').TrimEnd([char]92).ToLowerInvariant()
}

function Get-ManifestPath([string] $Path) {
    if ($Path) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\src\NemotronDiarizationSharp.Runtime.Cpu\cpu-runtime-manifest.json'))
}

function Get-ProjectPath([string] $Path) {
    if ($Path) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\src\NemotronDiarizationSharp.Runtime.Cpu\NemotronDiarizationSharp.Runtime.Cpu.csproj'))
}

function Get-EvaluatedItems([string] $Path) {
    $output = & dotnet msbuild $Path '-getItem:Content,None' -nologo 2>&1
    if ($LASTEXITCODE -ne 0) { throw "MSBuild item evaluation failed for '$Path': $($output -join [Environment]::NewLine)" }
    try { return ($output -join [Environment]::NewLine) | ConvertFrom-Json }
    catch { throw "MSBuild item evaluation did not return valid JSON for '$Path'." }
}

function Get-ItemMetadata($Item, [string] $Name) {
    $property = $Item.PSObject.Properties[$Name]
    if ($null -eq $property) { return '' }
    return [string]$property.Value
}

try {
    $manifestFullPath = Get-ManifestPath $ManifestPath
    $projectFullPath = Get-ProjectPath $ProjectPath
    Assert-Allowlist ([IO.File]::Exists($manifestFullPath)) "CPU manifest not found: $manifestFullPath"
    Assert-Allowlist ([IO.File]::Exists($projectFullPath)) "CPU runtime project not found: $projectFullPath"

    $manifest = [IO.File]::ReadAllText($manifestFullPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    Assert-Allowlist ($manifest.schemaVersion -eq 1) 'CPU manifest schemaVersion must remain 1.'
    $manifestNames = @($manifest.files | ForEach-Object { [string]$_.fileName })
    Assert-Allowlist ($manifestNames.Count -eq 18) "The committed CPU manifest must represent all 18 original DLLs; found $($manifestNames.Count)."
    Assert-Allowlist ((@($manifestNames | Sort-Object -Unique).Count) -eq 18) 'The CPU manifest contains duplicate DLL names.'
    foreach ($name in $originalInventory) {
        Assert-Allowlist ($manifestNames -ccontains $name) "The original CPU provenance inventory is missing '$name' from the manifest."
    }
    foreach ($name in $manifestNames) {
        Assert-Allowlist ($originalInventory -ccontains $name) "The CPU manifest contains an unexpected DLL '$name'."
    }
    foreach ($name in $allowlist) {
        $records = @($manifest.files | Where-Object { $_.fileName -ceq $name })
        Assert-Allowlist ($records.Count -eq 1) "The allowlisted DLL '$name' must have exactly one manifest record."
        Assert-Allowlist ($records[0].classification -ceq 'ENGINE') "The allowlisted DLL '$name' must retain its ENGINE classification."
    }

    [xml]$projectXml = [IO.File]::ReadAllText($projectFullPath, [Text.Encoding]::UTF8)
    foreach ($element in $projectXml.SelectNodes('//*')) {
        foreach ($attribute in $element.Attributes) {
            $value = [string]$attribute.Value
            if ($value -match '(?i)\.dll' -and $value -match '[*?]') {
                Assert-Allowlist $false "Native DLL wildcard detected in <$($element.LocalName)> $($attribute.Name)='$value'."
            }
        }
    }

    $evaluated = Get-EvaluatedItems $projectFullPath
    $nativeItems = @()
    foreach ($itemType in @('Content', 'None')) {
        foreach ($item in @($evaluated.Items.$itemType)) {
            $identity = [string]$item.Identity
            $fullPath = [string]$item.FullPath
            $packagePath = Normalize-Path (Get-ItemMetadata $item 'PackagePath')
            $isDll = [IO.Path]::GetExtension($identity) -ieq '.dll'
            $isNativeDll = $isDll -and (
                ((Normalize-Path $identity) -match '(^|[\\/])runtimes[\\/]win-x64[\\/]native[\\/].*\.dll$') -or
                ((Normalize-Path $fullPath) -match '[\\/]runtimes[\\/]win-x64[\\/]native[\\/].*\.dll$') -or
                ($packagePath -ceq $expectedPackagePath) -or
                ((Get-ItemMetadata $item 'Pack') -ceq 'true') -or
                -not [string]::IsNullOrWhiteSpace((Get-ItemMetadata $item 'CopyToOutputDirectory')) -or
                -not [string]::IsNullOrWhiteSpace((Get-ItemMetadata $item 'CopyToPublishDirectory'))
            )
            if ($isNativeDll) {
                Assert-Allowlist ($itemType -ceq 'Content') "Unexpected native DLL item type '$itemType' for '$identity'."
                $nativeItems += $item
            }
        }
    }

    $selectedNames = @($nativeItems | ForEach-Object { [IO.Path]::GetFileName([string]$_.Identity) })
    Assert-Allowlist ($selectedNames.Count -eq 5) "Expected exactly five evaluated native Content items; found $($selectedNames.Count)."
    Assert-Allowlist ((@($selectedNames | Sort-Object -Unique).Count) -eq 5) 'Evaluated native Content items contain duplicate filenames.'
    foreach ($name in $selectedNames) {
        Assert-Allowlist ($allowlist -ccontains $name) "Unexpected evaluated native Content item '$name'."
    }
    foreach ($name in $allowlist) {
        Assert-Allowlist ($selectedNames -ccontains $name) "Expected native Content item '$name' was not evaluated."
    }

    foreach ($item in $nativeItems) {
        $packagePath = Normalize-Path (Get-ItemMetadata $item 'PackagePath')
        Assert-Allowlist ($packagePath -ceq $expectedPackagePath) "Unexpected package path '$(Get-ItemMetadata $item 'PackagePath')' for '$($item.Identity)'; expected '$expectedPackagePath'."
        Assert-Allowlist ((Get-ItemMetadata $item 'Pack') -ceq 'true') "Native item '$($item.Identity)' is not marked for package inclusion."
    }

    $transitiveTargetsPath = Join-Path ([IO.Path]::GetDirectoryName($projectFullPath)) 'buildTransitive\NemotronDiarizationSharp.Runtime.Cpu.targets'
    Assert-Allowlist ([IO.File]::Exists($transitiveTargetsPath)) "CPU runtime buildTransitive target not found: $transitiveTargetsPath"
    [xml]$transitiveTargetsXml = [IO.File]::ReadAllText($transitiveTargetsPath, [Text.Encoding]::UTF8)
    $copyContent = $transitiveTargetsXml.SelectSingleNode('/Project/ItemGroup/Content')
    Assert-Allowlist ($null -ne $copyContent) 'The CPU runtime buildTransitive target must include its native content.'
    $copyInclude = Normalize-Path ([string]$copyContent.GetAttribute('Include'))
    Assert-Allowlist ($copyInclude.Contains($expectedPackagePath)) "CPU runtime buildTransitive target points outside '$expectedPackagePath': '$copyInclude'."
    Assert-Allowlist (([string]$copyContent.CopyToOutputDirectory) -ceq 'PreserveNewest') 'The CPU runtime buildTransitive target must preserve output-copy behavior.'
    Assert-Allowlist (([string]$copyContent.CopyToPublishDirectory) -ceq 'PreserveNewest') 'The CPU runtime buildTransitive target must preserve publish-copy behavior.'

    Write-Output 'PASS: metadata allowlist validation; exact five evaluated native Content items, no native DLL wildcard or duplicate, expected package/output metadata, and all 18 original manifest entries present.'
    Write-Output 'Evaluated pack metadata only. No .nupkg was created or inspected; actual package contents remain unverified.'

    if ($Mode -eq 'FullLocal') {
        if (-not $NativeDirectory) {
            $NativeDirectory = Join-Path ([IO.Path]::GetDirectoryName($manifestFullPath)) 'runtimes\win-x64\native'
        }
        $nativeRoot = [IO.Path]::GetFullPath($NativeDirectory)
        foreach ($name in $allowlist) {
            $record = @($manifest.files | Where-Object { $_.fileName -ceq $name })[0]
            $path = Join-Path $nativeRoot $name
            Assert-Allowlist ([IO.File]::Exists($path)) "Allowlisted native DLL is missing: $path"
            $stream = [IO.File]::OpenRead($path)
            try {
                Assert-Allowlist ($stream.Length -eq [long]$record.sizeBytes) "Size mismatch for '$name'."
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $actualHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
                finally { $sha.Dispose() }
                Assert-Allowlist ($actualHash -ieq [string]$record.sha256) "SHA-256 mismatch for '$name'."
            } finally { $stream.Dispose() }
            Write-Output "MATCH DLL: $name"
        }
        Write-Output 'PASS: full local allowlist hash verification; native DLLs were read only and never executed, copied or modified.'
    } else {
        Write-Output 'Full local DLL hashes were not requested.'
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

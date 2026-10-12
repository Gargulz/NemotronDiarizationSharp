[CmdletBinding()]
param(
    [string] $ManifestPath,
    [ValidateSet('Metadata', 'Artifacts')]
    [string] $Mode = 'Metadata',
    [string] $ArchivePath,
    [string] $NativeDirectory,
    [string] $LicenseDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Manifest([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Invalid CPU manifest: $Message" }
}

function Assert-Fields($Value, [string[]] $Names, [string] $Context) {
    Assert-Manifest ($Value -is [pscustomobject]) "$Context must be an object."
    foreach ($name in $Names) {
        Assert-Manifest ($Value.PSObject.Properties.Name -ccontains $name) "$Context is missing '$name'."
    }
}

function Assert-Hash($Value, [string] $Context) {
    Assert-Manifest ($Value -is [string] -and $Value -cmatch '\A[0-9A-Fa-f]{64}\z') "$Context must be a 64-character SHA-256."
}

function Assert-Size($Value, [string] $Context) {
    Assert-Manifest (($Value -is [int] -or $Value -is [long]) -and $Value -gt 0) "$Context must be a positive integer."
}

function Assert-Strings($Value, [string] $Context, [bool] $AllowEmpty = $false) {
    Assert-Manifest ($Value -is [array]) "$Context must be an array."
    Assert-Manifest ($AllowEmpty -or $Value.Count -gt 0) "$Context must not be empty."
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $Value) {
        Assert-Manifest ($item -is [string] -and -not [string]::IsNullOrWhiteSpace($item)) "$Context contains an empty or non-string entry."
        Assert-Manifest ($seen.Add($item)) "$Context contains duplicate '$item'."
    }
}

function Assert-Inventory([string[]] $Actual, [string[]] $Expected, [string] $Context) {
    Assert-Manifest ($Actual.Count -eq $Expected.Count) "$Context requires $($Expected.Count) entries, found $($Actual.Count)."
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $Actual) {
        Assert-Manifest ($seen.Add($name)) "$Context contains duplicate '$name'."
        Assert-Manifest ($Expected -ccontains $name) "$Context contains unexpected entry '$name'."
    }
    foreach ($name in $Expected) {
        Assert-Manifest ($Actual -ccontains $name) "$Context is missing '$name'."
    }
}

function Get-StreamHash([IO.Stream] $Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Assert-LocalFile([string] $Path, $Record, [string] $Context) {
    if (-not [IO.File]::Exists($Path)) { throw "Missing $Context." }
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -ne $Record.sizeBytes) { throw "Size mismatch for ${Context}: expected $($Record.sizeBytes), found $($stream.Length)." }
        $actual = Get-StreamHash $stream
        if ($actual -ine $Record.sha256) { throw "SHA-256 mismatch for ${Context}: expected $($Record.sha256), found $actual." }
    } finally { $stream.Dispose() }
}

try {
    if (-not $ManifestPath) {
        $ManifestPath = Join-Path $PSScriptRoot '..\..\src\NemotronDiarizationSharp.Runtime.Cpu\cpu-runtime-manifest.json'
    }
    $json = [IO.File]::ReadAllText([IO.Path]::GetFullPath($ManifestPath), [Text.Encoding]::UTF8)
    $manifest = $json | ConvertFrom-Json
    Assert-Fields $manifest @('schemaVersion', 'target', 'nativeRelease', 'archive', 'evidence', 'files', 'licenseFiles', 'dependencyAnalysis', 'externalRuntimePrerequisites', 'sourceReferences') 'root'
    Assert-Manifest ($manifest.schemaVersion -is [int] -and $manifest.schemaVersion -eq 1) 'Only schemaVersion 1 is supported.'
    Assert-Fields $manifest.target @('os', 'architecture', 'peMachine', 'peMachineCode') 'target'
    Assert-Manifest ($manifest.target.os -ceq 'windows' -and $manifest.target.architecture -ceq 'x64' -and $manifest.target.peMachine -ceq 'AMD64' -and $manifest.target.peMachineCode -ceq '0x8664') 'target must be Windows x64 / AMD64 (0x8664).'
    Assert-Fields $manifest.nativeRelease @('name', 'version', 'sourceCommit', 'llamaCppGitlink', 'sourceReferenceStatus') 'nativeRelease'
    Assert-Manifest ($manifest.nativeRelease.name -ceq 'NeMo-Speech.cpp' -and $manifest.nativeRelease.version -ceq '0.2.0') 'nativeRelease must identify NeMo-Speech.cpp v0.2.0.'
    Assert-Manifest ($manifest.nativeRelease.sourceCommit -ceq '6a3ca369370782acee0dd155e34394ba60b920c4') 'Unexpected pinned source commit.'
    Assert-Manifest ($manifest.nativeRelease.llamaCppGitlink -ceq 'bd4f514db14d87fded667787a7a963bfbaa98e89') 'Unexpected pinned llama.cpp gitlink.'
    Assert-Manifest ($manifest.nativeRelease.sourceReferenceStatus -ceq 'PINNED_REFERENCE_ONLY') 'Source references must not imply verified binary build provenance.'

    Assert-Fields $manifest.archive @('fileName', 'sizeBytes', 'sha256', 'entryPrefix', 'licenseEntryPrefix') 'archive'
    Assert-Manifest ($manifest.archive.fileName -ceq 'nemo-speech-0.2.0-windows-x86_64-cpu.zip') 'Unexpected archive filename.'
    Assert-Size $manifest.archive.sizeBytes 'archive.sizeBytes'
    Assert-Hash $manifest.archive.sha256 'archive.sha256'
    Assert-Manifest ($manifest.archive.entryPrefix -ceq 'nemo-speech-0.2.0-windows-x86_64-cpu/bin/') 'Unexpected DLL archive prefix.'
    Assert-Manifest ($manifest.archive.licenseEntryPrefix -ceq 'nemo-speech-0.2.0-windows-x86_64-cpu/share/licenses/nemo-speech/') 'Unexpected license archive prefix.'

    Assert-Fields $manifest.evidence @('basis', 'archiveToFileIdentity', 'comparisonMethod', 'importEvidence', 'nativeBuildProvenance', 'toolchainProvenance', 'kenlmLinkage', 'staticLinkedComponentVersions', 'dependencyClosure', 'licenseCoverage', 'redistribution', 'removalAuthorized', 'limitations') 'evidence'
    Assert-Manifest ($manifest.evidence.basis -ceq 'A7.1R' -and $manifest.evidence.archiveToFileIdentity -ceq 'VERIFIED' -and $manifest.evidence.comparisonMethod -ceq 'FULL_BYTE_COMPARISON_AND_SHA256' -and $manifest.evidence.importEvidence -ceq 'OBSERVED_PE_IMPORT_TABLES') 'Unexpected evidence basis or method.'
    foreach ($field in @('nativeBuildProvenance', 'toolchainProvenance', 'kenlmLinkage', 'staticLinkedComponentVersions', 'dependencyClosure')) {
        Assert-Manifest ($manifest.evidence.$field -ceq 'UNKNOWN') "evidence.$field must preserve UNKNOWN."
    }
    Assert-Manifest ($manifest.evidence.licenseCoverage -ceq 'NEEDS_REVIEW' -and $manifest.evidence.redistribution -ceq 'BLOCKED') 'License review and redistribution block must be preserved.'
    Assert-Manifest ($manifest.evidence.removalAuthorized -is [bool] -and -not $manifest.evidence.removalAuthorized) 'Removal must remain unauthorized.'
    Assert-Strings $manifest.evidence.limitations 'evidence.limitations'
    Assert-Strings $manifest.sourceReferences 'sourceReferences'
    foreach ($reference in $manifest.sourceReferences) {
        Assert-Manifest ($reference -cmatch '\Ahttps://') 'Source references must be HTTPS URLs, not local machine paths.'
    }

    $engineNames = @('nemo_speech_asr_c.dll', 'nemo_speech_asr.dll', 'ggml.dll', 'ggml-base.dll', 'ggml-cpu.dll')
    $crtNames = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcomp140.dll')
    $otherEngineNames = @('llama.dll', 'nemo_speech_nmt.dll', 'nemo_speech_nmt_c.dll', 'nemo_speech_tts.dll')
    $extraCrtNames = @('concrt140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll')
    $dllNames = $engineNames + $crtNames + $otherEngineNames + $extraCrtNames
    $licenseNames = @('LICENSE', 'NOTICE', 'THIRD_PARTY_NOTICES.md', 'third_party/cpp-httplib/LICENSE', 'third_party/ggml/LICENSE', 'third_party/llama.cpp/LICENSE', 'third_party/miniaudio/LICENSE', 'third_party/vcpkg/abseil/LICENSE', 'third_party/vcpkg/protobuf/LICENSE', 'third_party/vcpkg/sentencepiece/LICENSE', 'third_party/vcpkg/utf8-range/LICENSE')
    Assert-Manifest ($manifest.files -is [array] -and $manifest.licenseFiles -is [array]) 'files and licenseFiles must be arrays.'
    foreach ($file in $manifest.files) {
        Assert-Fields $file @('fileName', 'sha256', 'sizeBytes', 'archiveEntry', 'provenance', 'classification', 'diarizationDependency', 'observedFileVersion', 'observedLinkerVersion', 'peMachineCode', 'imports', 'dynamicLoadImports') 'files entry'
        Assert-Manifest ($file.fileName -is [string] -and $dllNames -ccontains $file.fileName) "Unexpected DLL '$($file.fileName)'."
        Assert-Hash $file.sha256 "DLL $($file.fileName).sha256"
        Assert-Size $file.sizeBytes "DLL $($file.fileName).sizeBytes"
        Assert-Manifest ($file.archiveEntry -ceq ($manifest.archive.entryPrefix + $file.fileName)) "Incorrect archive entry for DLL $($file.fileName)."
        Assert-Manifest ($file.provenance -ceq 'BYTE_MATCHED_TO_RELEASE_ZIP') "Unexpected provenance for DLL $($file.fileName)."
        $classification = if ($engineNames -ccontains $file.fileName) { 'ENGINE' } elseif ($otherEngineNames -ccontains $file.fileName) { 'OTHER_ENGINE' } else { 'MICROSOFT_RUNTIME' }
        Assert-Manifest ($file.classification -ceq $classification) "Incorrect classification for DLL $($file.fileName)."
        $dependency = if (($engineNames + $crtNames) -ccontains $file.fileName) { 'OBSERVED_STATIC_IMPORT_CLOSURE' } else { 'OUTSIDE_OBSERVED_STATIC_IMPORT_CLOSURE' }
        Assert-Manifest ($file.diarizationDependency -ceq $dependency) "Incorrect dependency classification for DLL $($file.fileName)."
        Assert-Manifest ($file.peMachineCode -ceq '0x8664' -and $file.observedLinkerVersion -ceq '14.44') "Unexpected observed PE metadata for DLL $($file.fileName)."
        Assert-Manifest ($null -eq $file.observedFileVersion -or ($file.observedFileVersion -is [string] -and $file.observedFileVersion -cmatch '\A\d+\.\d+\.\d+\.\d+\z')) "Invalid file version for DLL $($file.fileName)."
        Assert-Strings $file.imports "DLL $($file.fileName).imports"
        foreach ($import in $file.imports) {
            Assert-Manifest ($import -imatch '\A[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*\.dll\z') "Invalid import '$import' in DLL $($file.fileName)."
        }
        Assert-Strings $file.dynamicLoadImports "DLL $($file.fileName).dynamicLoadImports" $true
        foreach ($import in $file.dynamicLoadImports) {
            Assert-Manifest (@('LoadLibraryW', 'LoadLibraryExW', 'GetProcAddress') -ccontains $import) "Unexpected dynamic load import '$import' in DLL $($file.fileName)."
        }
    }
    Assert-Inventory @($manifest.files | ForEach-Object fileName) $dllNames 'DLL inventory'
    foreach ($file in $manifest.licenseFiles) {
        Assert-Fields $file @('path', 'sha256', 'sizeBytes', 'archiveEntry', 'provenance') 'licenseFiles entry'
        Assert-Manifest ($file.path -is [string] -and $licenseNames -ccontains $file.path) "Unexpected license path '$($file.path)'."
        Assert-Hash $file.sha256 "License $($file.path).sha256"
        Assert-Size $file.sizeBytes "License $($file.path).sizeBytes"
        Assert-Manifest ($file.archiveEntry -ceq ($manifest.archive.licenseEntryPrefix + $file.path)) "Incorrect archive entry for license $($file.path)."
        Assert-Manifest ($file.provenance -ceq 'BYTE_MATCHED_TO_RELEASE_ZIP') "Unexpected provenance for license $($file.path)."
    }
    Assert-Inventory @($manifest.licenseFiles | ForEach-Object path) $licenseNames 'License inventory'

    Assert-Fields $manifest.dependencyAnalysis @('entryDll', 'engineCandidate', 'microsoftRuntimeCandidate', 'candidateStatus', 'loadedModuleEvidence', 'potentialDynamicDependencies') 'dependencyAnalysis'
    Assert-Manifest ($manifest.dependencyAnalysis.entryDll -ceq 'nemo_speech_asr_c.dll' -and $manifest.dependencyAnalysis.candidateStatus -ceq 'PROVISIONAL_NOT_RUNTIME_VALIDATED' -and $manifest.dependencyAnalysis.loadedModuleEvidence -ceq 'NOT_AVAILABLE') 'Dependency candidate must remain provisional.'
    Assert-Strings $manifest.dependencyAnalysis.engineCandidate 'engineCandidate'
    Assert-Strings $manifest.dependencyAnalysis.microsoftRuntimeCandidate 'microsoftRuntimeCandidate'
    Assert-Inventory $manifest.dependencyAnalysis.engineCandidate $engineNames 'Engine candidate'
    Assert-Inventory $manifest.dependencyAnalysis.microsoftRuntimeCandidate $crtNames 'Microsoft runtime candidate'
    $byName = @{}
    foreach ($file in $manifest.files) { $byName[$file.fileName] = $file }
    $closure = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($manifest.dependencyAnalysis.entryDll)
    while ($pending.Count -gt 0) {
        $name = $pending.Dequeue()
        if ($closure.Add($name)) {
            foreach ($import in $byName[$name].imports) {
                if ($byName.ContainsKey($import)) { $pending.Enqueue($byName[$import].fileName) }
            }
        }
    }
    Assert-Inventory @($closure) ($engineNames + $crtNames) 'Declared static import closure'
    Assert-Manifest ($manifest.dependencyAnalysis.potentialDynamicDependencies -is [array] -and $manifest.dependencyAnalysis.potentialDynamicDependencies.Count -gt 0) 'Dynamic dependency limitations are required.'
    foreach ($item in $manifest.dependencyAnalysis.potentialDynamicDependencies) {
        Assert-Fields $item @('fileName', 'evidence', 'status') 'potentialDynamicDependencies entry'
        Assert-Manifest (($dllNames + 'MICROSOFT_RUNTIME') -ccontains $item.fileName -and $item.status -ceq 'UNKNOWN') 'Dynamic dependency status must remain UNKNOWN.'
        Assert-Strings $item.evidence "Dynamic dependency $($item.fileName).evidence"
    }
    Assert-Manifest ($manifest.externalRuntimePrerequisites -is [array]) 'externalRuntimePrerequisites must be an array.'
    foreach ($item in $manifest.externalRuntimePrerequisites) { Assert-Fields $item @('id', 'status', 'deploymentStrategy') 'externalRuntimePrerequisites entry' }
    Assert-Inventory @($manifest.externalRuntimePrerequisites | ForEach-Object id) @('msvc-v14-x64', 'windows-system-libraries') 'External runtime prerequisites'
    $msvc = @($manifest.externalRuntimePrerequisites | Where-Object id -eq 'msvc-v14-x64')[0]
    Assert-Fields $msvc @('architecture', 'importedDlls', 'observedBundledFileVersion', 'minimumVersion', 'minimumVersionStatus', 'redistributionRights') 'MSVC prerequisite'
    Assert-Manifest ($msvc.status -ceq 'PROPOSED' -and $msvc.deploymentStrategy -ceq 'CENTRAL_VISUAL_CPP_REDISTRIBUTABLE_PREREQUISITE' -and $msvc.architecture -ceq 'x64') 'Unexpected proposed MSVC deployment strategy.'
    Assert-Manifest ($msvc.observedBundledFileVersion -ceq '14.44.35211.0' -and $null -eq $msvc.minimumVersion -and $msvc.minimumVersionStatus -ceq 'UNKNOWN' -and $msvc.redistributionRights -ceq 'NEEDS_REVIEW') 'MSVC minimum version and rights must remain unresolved.'
    Assert-Strings $msvc.importedDlls 'MSVC prerequisite.importedDlls'
    Assert-Inventory $msvc.importedDlls $crtNames 'MSVC prerequisite imports'
    $system = @($manifest.externalRuntimePrerequisites | Where-Object id -eq 'windows-system-libraries')[0]
    Assert-Fields $system @('importFamilies') 'Windows system prerequisite'
    Assert-Manifest ($system.status -ceq 'OBSERVED_IMPORTS' -and $system.deploymentStrategy -ceq 'OPERATING_SYSTEM_COMPONENTS') 'Unexpected Windows system prerequisite.'
    Assert-Strings $system.importFamilies 'Windows system import families'

    if ($Mode -eq 'Metadata') {
        if ($ArchivePath -or $NativeDirectory -or $LicenseDirectory) { throw 'Artifact paths require -Mode Artifacts.' }
        Write-Host 'PASS: metadata only; schema 1, 18 DLL entries, 11 release license entries. No artifact hashes or provenance were reverified.'
        exit 0
    }
    if (-not ($ArchivePath -or $NativeDirectory -or $LicenseDirectory)) { throw '-Mode Artifacts requires at least one explicit artifact path.' }

    if ($ArchivePath) {
        Assert-LocalFile ([IO.Path]::GetFullPath($ArchivePath)) $manifest.archive 'release ZIP'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
        try {
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $zip.Entries) {
                if (-not $seen.Add($entry.FullName)) { throw "Duplicate ZIP entry '$($entry.FullName)'." }
            }
            $zipDllNames = @($zip.Entries | Where-Object { $_.FullName.StartsWith($manifest.archive.entryPrefix, [StringComparison]::Ordinal) -and $_.FullName.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $_.FullName.Substring($manifest.archive.entryPrefix.Length) })
            Assert-Inventory $zipDllNames $dllNames 'ZIP DLL inventory'
            $zipLicenseNames = @($zip.Entries | Where-Object { $_.FullName.StartsWith($manifest.archive.licenseEntryPrefix, [StringComparison]::Ordinal) -and -not $_.FullName.EndsWith('/') } | ForEach-Object { $_.FullName.Substring($manifest.archive.licenseEntryPrefix.Length) })
            Assert-Inventory $zipLicenseNames $licenseNames 'ZIP license inventory'
            foreach ($file in ($manifest.files + $manifest.licenseFiles)) {
                $entry = $zip.GetEntry($file.archiveEntry)
                if ($null -eq $entry) { throw "Missing ZIP entry '$($file.archiveEntry)'." }
                if ($entry.Length -ne $file.sizeBytes) { throw "Size mismatch for ZIP entry '$($file.archiveEntry)'." }
                $stream = $entry.Open()
                try { $actual = Get-StreamHash $stream } finally { $stream.Dispose() }
                if ($actual -ine $file.sha256) { throw "SHA-256 mismatch for ZIP entry '$($file.archiveEntry)': expected $($file.sha256), found $actual." }
                Write-Host "MATCH ZIP: $($file.archiveEntry)"
            }
        } finally { $zip.Dispose() }
    }
    if ($NativeDirectory) {
        $directory = [IO.Path]::GetFullPath($NativeDirectory)
        $actualNames = @([IO.Directory]::GetFiles($directory, '*.dll') | ForEach-Object { [IO.Path]::GetFileName($_) })
        Assert-Inventory $actualNames $dllNames 'Staged DLL inventory'
        foreach ($file in $manifest.files) {
            Assert-LocalFile (Join-Path $directory $file.fileName) $file "staged DLL '$($file.fileName)'"
            Write-Host "MATCH DLL: $($file.fileName)"
        }
    }
    if ($LicenseDirectory) {
        $directory = [IO.Path]::GetFullPath($LicenseDirectory)
        # RUNTIME_COMPONENTS.md is repository explanation, not an original ZIP license.
        foreach ($file in $manifest.licenseFiles) {
            Assert-LocalFile (Join-Path $directory $file.path) $file "staged license '$($file.path)'"
            Write-Host "MATCH LICENSE: $($file.path)"
        }
    }
    if ($ArchivePath -and $NativeDirectory -and $LicenseDirectory) {
        Write-Host 'PASS: full artifact hash verification; ZIP, 18 staged DLLs and 11 staged release licenses match the manifest. Build provenance and redistribution remain unresolved.'
    } else {
        Write-Host "PASS: partial artifact hash verification (ZIP=$([bool]$ArchivePath), DLLs=$([bool]$NativeDirectory), licenses=$([bool]$LicenseDirectory)); omitted artifact sets were not verified."
    }
} catch {
    [Console]::Error.WriteLine("CPU runtime manifest verification failed: $($_.Exception.Message)")
    exit 1
}

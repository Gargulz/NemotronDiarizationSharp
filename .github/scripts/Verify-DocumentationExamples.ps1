[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sdkProjectPath = Join-Path $repositoryRoot 'src\NemotronDiarizationSharp\NemotronDiarizationSharp.csproj'
$documentationFiles = @(
    'README.md',
    'README.ru.md',
    'docs/en/overview.md',
    'docs/ru/overview.md'
)
$exampleMarkerPattern = '^<!-- compile-example: ([a-z0-9-]+); scaffold=([a-z0-9-]+) -->$'
$examples = [System.Collections.Generic.List[object]]::new()

foreach ($relativePath in $documentationFiles) {
    $markdownPath = Join-Path $repositoryRoot $relativePath
    $lines = [System.IO.File]::ReadAllText($markdownPath, [System.Text.Encoding]::UTF8) -split '\r?\n'

    for ($index = 0; $index -lt $lines.Length; $index++) {
        if ($lines[$index] -match $exampleMarkerPattern) {
            $exampleId = $Matches[1]
            $scaffold = $Matches[2]
            $openingIndex = $index + 1
            if ($openingIndex -ge $lines.Length -or $lines[$openingIndex] -cne '```csharp') {
                throw "Compile-example marker '$exampleId' in $relativePath must immediately precede a csharp fence."
            }

            $closingIndex = $openingIndex + 1
            while ($closingIndex -lt $lines.Length -and $lines[$closingIndex] -cne '```') {
                $closingIndex++
            }
            if ($closingIndex -ge $lines.Length) {
                throw "Compile-example '$exampleId' in $relativePath has no closing code fence."
            }

            $codeLines = [System.Collections.Generic.List[string]]::new()
            for ($codeIndex = $openingIndex + 1; $codeIndex -lt $closingIndex; $codeIndex++) {
                $codeLines.Add($lines[$codeIndex])
            }

            $examples.Add([pscustomobject]@{
                Id = $exampleId
                Scaffold = $scaffold
                RelativePath = $relativePath.Replace('\', '/')
                StartLine = $openingIndex + 2
                CodeLines = $codeLines
            })
            $index = $closingIndex
            continue
        }

        if ($lines[$index] -cmatch '^```csharp\s*$') {
            throw "Unmarked C# code fence in $relativePath at line $($index + 1). Add a compile-example marker."
        }
    }
}

if ($examples.Count -eq 0) {
    throw 'No marked documentation examples were found.'
}

$duplicateIds = @($examples | Group-Object Id | Where-Object Count -gt 1)
if ($duplicateIds.Count -gt 0) {
    $names = ($duplicateIds | ForEach-Object Name) -join ', '
    throw "Compile-example IDs must be unique across documentation files: $names"
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "NemotronDiarizationSharp-doc-examples-$([guid]::NewGuid().ToString('N'))"
if (Test-Path -LiteralPath $temporaryRoot) {
    throw "Refusing to overwrite existing temporary directory: $temporaryRoot"
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

try {
    $generatedSource = [System.Collections.Generic.List[string]]::new()
    $generatedSource.Add('using System;')
    $generatedSource.Add('using System.Collections.Generic;')
    $generatedSource.Add('using NemotronDiarizationSharp;')
    $generatedSource.Add('')
    $generatedSource.Add('internal static class DocumentationExamples')
    $generatedSource.Add('{')

    foreach ($example in $examples) {
        $methodName = 'Compile_' + ($example.Id -replace '[^A-Za-z0-9_]', '_')
        $generatedSource.Add("    internal static void $methodName()")
        $generatedSource.Add('    {')

        switch ($example.Scaffold) {
            'offline' {
                $prelude = @()
                $epilogue = @('static float[] LoadMonoFloat32Pcm() => Array.Empty<float>();')
            }
            'streaming' {
                $prelude = @(
                    'string modelPath = "models/Nemotron-3-Diarization.q8_0.gguf";',
                    'const int sampleRate = 16_000;'
                )
                $epilogue = @('static IEnumerable<ReadOnlyMemory<float>> GetMonoPcmChunks() => Array.Empty<ReadOnlyMemory<float>>();')
            }
            'model-path' {
                $prelude = @('string modelPath = "models/Nemotron-3-Diarization.q8_0.gguf";')
                $epilogue = @()
            }
            default {
                throw "Unknown scaffold '$($example.Scaffold)' for $($example.RelativePath) compile-example '$($example.Id)'."
            }
        }

        foreach ($line in $prelude) {
            $generatedSource.Add("        $line")
        }

        $codeLines = [string[]]$example.CodeLines.ToArray()
        for ($lineIndex = 0; $lineIndex -lt $codeLines.Length; $lineIndex++) {
            if ($codeLines[$lineIndex] -match '^\s*using\s+NemotronDiarizationSharp;\s*$') {
                $codeLines[$lineIndex] = ''
            }
            elseif ($codeLines[$lineIndex] -match '^\s*using\s+[A-Za-z_][A-Za-z0-9_.]*;\s*$') {
                throw "Unexpected using directive in $($example.RelativePath) compile-example '$($example.Id)' at line $($example.StartLine + $lineIndex)."
            }
        }

        $escapedPath = $example.RelativePath.Replace('"', '\"')
        $generatedSource.Add("#line $($example.StartLine) `"$escapedPath`"")
        foreach ($line in $codeLines) {
            $generatedSource.Add($line)
        }
        $generatedSource.Add('#line default')

        foreach ($line in $epilogue) {
            $generatedSource.Add("        $line")
        }

        $generatedSource.Add('    }')
        $generatedSource.Add('')
        Write-Host "Compiling $($example.RelativePath):$($example.StartLine) ($($example.Id))"
    }

    $generatedSource.Add('}')

    $generatedProjectPath = Join-Path $temporaryRoot 'DocumentationExamples.csproj'
    $generatedSourcePath = Join-Path $temporaryRoot 'DocumentationExamples.cs'
    $escapedProjectPath = [System.Security.SecurityElement]::Escape($sdkProjectPath)
    $generatedProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$escapedProjectPath" />
  </ItemGroup>
</Project>
"@

    $utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($generatedProjectPath, $generatedProject, $utf8WithoutBom)
    [System.IO.File]::WriteAllLines($generatedSourcePath, $generatedSource, $utf8WithoutBom)

    & dotnet build $generatedProjectPath --configuration Release --verbosity minimal
    $buildExitCode = $LASTEXITCODE
    if ($buildExitCode -ne 0) {
        throw "Documentation example compilation failed with exit code $buildExitCode. Compiler diagnostics map to the source Markdown file and line above."
    }

    Write-Host "Compiled $($examples.Count) marked documentation examples. The generated assembly was not executed."
}
finally {
    $temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTemporaryRoot.StartsWith($temporaryBase, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemporaryRoot) -match '^NemotronDiarizationSharp-doc-examples-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}

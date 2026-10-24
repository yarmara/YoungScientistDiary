# SPDX-License-Identifier: Apache-2.0
# Windows PowerShell 5.1 / PowerShell 7. No downloads and no system changes.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,
    [string] $ProjectFile = (Join-Path $PSScriptRoot '..\src\YoungScientistDiary.App\YoungScientistDiary.App.csproj'),
    [string] $Configuration = 'Release',
    [string] $RuntimeIdentifier = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $publishPath = (Resolve-Path -LiteralPath $PublishDirectory).ProviderPath
    $projectPath = (Resolve-Path -LiteralPath $ProjectFile).ProviderPath
    $depsPath = Join-Path $publishPath 'YoungScientistDiary.deps.json'
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
        throw "The published dependencies file does not exist: $depsPath"
    }
    $dependencies = Get-Content -LiteralPath $depsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $publishedPacks = @($dependencies.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'runtimepack' })
    if ($publishedPacks.Count -eq 0) {
        throw 'No runtime packs were found in the published .deps.json. Publish with --self-contained true first.'
    }

    # Resolve the exact pack directories selected by this project and SDK. The
    # published .deps.json is authoritative for the versions actually shipped.
    # ResolveReferences can also expose unused packs, which are not copied.
    $dotnetCommand = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    $arguments = @(
        'msbuild', $projectPath, '-nologo', '-target:ResolveReferences',
        '-getItem:ResolvedRuntimePack',
        "-property:Configuration=$Configuration",
        '-property:SelfContained=true'
    )
    $rawOutput = & $dotnetCommand @arguments
    if ($LASTEXITCODE -ne 0) { throw "MSBuild could not resolve runtime packs (exit $LASTEXITCODE)." }
    $jsonOutput = $rawOutput -join [Environment]::NewLine
    # MSBuild warnings can precede the JSON result. Do not interpret them as JSON.
    $jsonStart = $jsonOutput.IndexOf('{')
    $jsonEnd = $jsonOutput.LastIndexOf('}')
    if ($jsonStart -lt 0 -or $jsonEnd -le $jsonStart) { throw 'MSBuild did not return runtime-pack metadata.' }
    $resolved = $jsonOutput.Substring($jsonStart, $jsonEnd - $jsonStart + 1) | ConvertFrom-Json
    $resolvedPacks = @($resolved.Items.ResolvedRuntimePack)

    $requiredIds = @("Microsoft.NETCore.App.Runtime.$RuntimeIdentifier", "Microsoft.WindowsDesktop.App.Runtime.$RuntimeIdentifier")
    foreach ($requiredId in $requiredIds) {
        $matching = @($publishedPacks | Where-Object { $_.Name.StartsWith("runtimepack.$requiredId/", [StringComparison]::OrdinalIgnoreCase) })
        if ($matching.Count -ne 1) { throw "The publication must contain exactly one $requiredId runtime pack." }
    }

    $destinationRoot = Join-Path $publishPath 'licenses/dotnet-runtime'
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
    $manifestPacks = @()

    foreach ($publishedPack in $publishedPacks) {
        if ($publishedPack.Name -notmatch '^runtimepack\.(?<Package>[^/]+)/(?<Version>[^/]+)$') {
            throw "Unrecognized runtime-pack identity: $($publishedPack.Name)"
        }
        $packageId = $Matches.Package
        $packageVersion = $Matches.Version
        if ($packageId -notmatch '^[A-Za-z0-9_.-]+$' -or $packageVersion -notmatch '^[A-Za-z0-9_.+-]+$') {
            throw 'Invalid characters in the published runtime-pack identity.'
        }
        $matchingResolved = @($resolvedPacks | Where-Object {
            $_.NuGetPackageId -eq $packageId -and $_.NuGetPackageVersion -eq $packageVersion
        })
        if ($matchingResolved.Count -ne 1) {
            throw "Published $packageId/$packageVersion does not match a single resolved runtime pack. Publish and copy notices using the same SDK and configuration."
        }
        $packDirectory = $matchingResolved[0].PackageDirectory
        if ([string]::IsNullOrWhiteSpace($packDirectory) -or -not (Test-Path -LiteralPath $packDirectory -PathType Container)) {
            throw "The exact runtime-pack directory is unavailable: $packageId/$packageVersion"
        }

        $packFiles = @(Get-ChildItem -LiteralPath $packDirectory -File)
        $licenseFiles = @($packFiles | Where-Object { $_.Name -match '^LICENSE(?:\.(?:TXT|MD))?$' })
        $noticeFiles = @($packFiles | Where-Object { $_.Name -match '^THIRD[-_ ]?PARTY[-_ ]?NOTICES?(?:\.(?:TXT|MD))?$' })
        if ($licenseFiles.Count -eq 0) { throw "LICENSE is absent from $packageId/$packageVersion." }
        if ($packageId -eq "Microsoft.NETCore.App.Runtime.$RuntimeIdentifier" -and $noticeFiles.Count -eq 0) {
            throw "Third-party notices are absent from $packageId/$packageVersion."
        }

        $folderName = "$packageId-$packageVersion"
        $packDestination = Join-Path $destinationRoot $folderName
        New-Item -ItemType Directory -Path $packDestination -Force | Out-Null
        $copiedFiles = @()
        foreach ($file in @($licenseFiles) + @($noticeFiles)) {
            $target = Join-Path $packDestination $file.Name
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
            $copiedFiles += [ordered]@{
                File = "$folderName/$($file.Name)"
                SHA256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        # WindowsDesktop runtime packs currently ship LICENSE without a separate
        # ThirdPartyNotices file. Record the absence; do not invent a document or
        # substitute a different SDK/runtime's notices. Any supplied notice is copied.
        $noticeStatus = if ($noticeFiles.Count -gt 0) { 'Copied from the exact runtime pack.' } else { 'The exact runtime pack does not contain a separate third-party-notices file; its supplied LICENSE is included.' }
        $manifestPacks += [ordered]@{
            PackageId = $packageId
            Version = $packageVersion
            ThirdPartyNotices = $noticeStatus
            Files = @($copiedFiles)
        }
        Write-Host "Runtime license documents: $packageId/$packageVersion ($($copiedFiles.Count) files)"
    }

    $manifest = [ordered]@{
        RuntimeIdentifier = $RuntimeIdentifier
        DependenciesFile = 'YoungScientistDiary.deps.json'
        DependenciesSHA256 = (Get-FileHash -LiteralPath $depsPath -Algorithm SHA256).Hash.ToLowerInvariant()
        GeneratedUtc = [DateTime]::UtcNow.ToString('o')
        Packs = @($manifestPacks)
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 12
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $destinationRoot 'runtime-notices-manifest.json'), $manifestJson, $utf8)
    Write-Host "Runtime notices saved to: $destinationRoot"
    exit 0
}
catch {
    [Console]::Error.WriteLine('Runtime notice copy failed: ' + $_.Exception.Message)
    exit 1
}

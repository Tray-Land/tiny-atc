[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # Root of the repository to build. Defaults to the folder this script lives in.
    [string]$RepoRoot,

    # The packaged app project, as a .csproj path or a project name. Only needed when
    # the repo has more than one project with a Package.appxmanifest next to it.
    [string]$Project,

    # Release notes for the GitHub release. When omitted, GitHub generates notes
    # from the commits/PRs since the previous release.
    [string]$Notes,

    # Mark the GitHub release as a pre-release.
    [switch]$Prerelease,

    # Build and sign only; skip tagging and the GitHub release.
    [switch]$SkipRelease,

    # Build unsigned artifacts for local checks only; requires -SkipRelease.
    [switch]$NoSign,

    # Override the default AppPackages\GitHub artifact directory.
    [string]$OutputDirectory,

    # Allow releasing with uncommitted changes (they would be in the build but not in the tag).
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ($NoSign -and -not $SkipRelease) {
    throw '-NoSign requires -SkipRelease; unsigned bundles cannot be released.'
}

# A packaged app project is a .csproj with a Package.appxmanifest next to it
# (test and library projects have none). Returns the matching .csproj files under $Root.
function Find-PackagedProject {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    Get-ChildItem -LiteralPath $Root -Filter '*.csproj' -File -Recurse -Depth 3 -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|AppPackages)\\' } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.DirectoryName 'Package.appxmanifest') } |
        Where-Object { -not $Project -or $_.BaseName -eq $Project }
}

# An explicit .csproj path pins both the project and (via git) the repo root.
$projectFile = $null
if ($Project -and $Project.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) {
    if (-not (Test-Path -LiteralPath $Project)) {
        throw "Project not found: $Project"
    }
    $projectFile = Get-Item -LiteralPath $Project
    if (-not $RepoRoot) {
        $RepoRoot = & git -C $projectFile.DirectoryName rev-parse --show-toplevel 2>$null
        if (-not $RepoRoot) {
            $RepoRoot = $projectFile.DirectoryName
        }
    }
}

# $PSScriptRoot is not reliable in every host (e.g. ISE/VS Code run-selection or
# dot-sourcing report the caller's folder), so resolve the root explicitly: an
# explicit -RepoRoot wins, then the script's own folder, then the git repo of the
# current directory. Only accept a candidate that actually contains a packaged project.
if (-not $RepoRoot) {
    $candidates = @(
        $(if ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path }),
        $PSScriptRoot,
        $(& git rev-parse --show-toplevel 2>$null),
        (Get-Location).ProviderPath
    ) | Where-Object { $_ }

    $RepoRoot = $candidates |
        Where-Object { Find-PackagedProject -Root $_ } |
        Select-Object -First 1

    if (-not $RepoRoot) {
        throw "Could not locate a repository with a packaged app project (tried: $($candidates -join ', ')). Pass -RepoRoot or run from inside the repo."
    }
}

if (-not (Test-Path -LiteralPath $RepoRoot)) {
    throw "Repository root not found: $RepoRoot"
}

$repoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath

if (-not $projectFile) {
    $found = @(Find-PackagedProject -Root $repoRoot)
    if ($found.Count -eq 0) {
        $what = if ($Project) { "named '$Project'" } else { 'with a Package.appxmanifest next to it' }
        throw "No project $what was found under $repoRoot."
    }
    if ($found.Count -gt 1) {
        throw "Found more than one packaged project; pass -Project with one of: $(($found | ForEach-Object BaseName) -join ', ')."
    }
    $projectFile = $found[0]
}

$appName = $projectFile.BaseName
$projectDir = $projectFile.DirectoryName
$projectPath = $projectFile.FullName
$manifestPath = Join-Path $projectDir 'Package.appxmanifest'
$outputRoot = if ($OutputDirectory) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    Join-Path $projectDir 'AppPackages\GitHub'
}
$publishProfilesRoot = Join-Path $projectDir 'Properties\PublishProfiles'

# Signing is done by the maintainer's local 'sign-msix' profile function, so the
# signing configuration stays out of the repo.
$signCommand = 'sign-msix'

$builds = @(
    @{
        Name = 'x64'
        Platform = 'x64'
        RuntimeIdentifier = 'win-x64'
        PublishProfile = 'win-x64.pubxml'
        OutputDir = (Join-Path $outputRoot 'x64')
    },
    @{
        Name = 'arm64'
        Platform = 'ARM64'
        RuntimeIdentifier = 'win-arm64'
        PublishProfile = 'win-arm64.pubxml'
        OutputDir = (Join-Path $outputRoot 'arm64')
    }
)

function Get-ReleaseTag {
    param(
        [Parameter(Mandatory)]
        [string]$Version
    )

    # 0.1.0.0 -> v0.1.0 (matches existing tags); keep a non-zero fourth part.
    $parts = $Version.Split('.')
    if ($parts.Count -eq 4 -and $parts[3] -eq '0') {
        $parts = $parts[0..2]
    }
    return 'v' + ($parts -join '.')
}

function Assert-ReleaseReady {
    param(
        [Parameter(Mandatory)]
        [string]$Tag
    )

    foreach ($tool in 'git', 'gh') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "$tool is required but was not found on PATH."
        }
    }

    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'gh is not authenticated. Run "gh auth login" first.'
    }

    $branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -eq 'HEAD') {
        throw 'Could not determine the current branch (detached HEAD?).'
    }

    if (-not $AllowDirty) {
        $status = & git -C $repoRoot status --porcelain
        if ($status) {
            throw "Working tree has uncommitted changes; commit/stash them or pass -AllowDirty.`n$($status -join "`n")"
        }
    }

    & git -C $repoRoot fetch origin --quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'git fetch origin failed.'
    }

    $upstream = & git -C $repoRoot rev-parse --abbrev-ref '@{u}' 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Branch '$branch' has no upstream. Push it first (git push -u origin $branch)."
    }
    $ahead = [int](& git -C $repoRoot rev-list --count '@{u}..HEAD')
    if ($ahead -gt 0) {
        throw "Branch '$branch' is $ahead commit(s) ahead of $upstream. Push before releasing."
    }

    & git -C $repoRoot rev-parse -q --verify "refs/tags/$Tag" *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Tag $Tag already exists locally. Bump the version in Package.appxmanifest."
    }
    $remoteTag = & git -C $repoRoot ls-remote --tags origin "refs/tags/$Tag"
    if ($remoteTag) {
        throw "Tag $Tag already exists on origin. Bump the version in Package.appxmanifest."
    }

    # Drafts have no tag on origin yet, so also check for an existing (draft) release.
    Push-Location -LiteralPath $repoRoot
    try {
        # Filter in PowerShell rather than --jq: Windows PowerShell 5.1 strips embedded quotes from native args.
        $releasesJson = & gh release list --limit 100 --json tagName
        if ($LASTEXITCODE -ne 0) {
            throw 'gh release list failed.'
        }
    }
    finally {
        Pop-Location
    }
    $existingRelease = ($releasesJson | Out-String | ConvertFrom-Json) | Where-Object { $_.tagName -eq $Tag }
    if ($existingRelease) {
        throw "A release for $Tag already exists on GitHub (possibly a draft). Delete it or bump the version."
    }

    return $branch
}

function Invoke-GitHubMsixBuild {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Build
    )

    $appxPackageDir = (Join-Path $Build.OutputDir '')

    if (Test-Path -LiteralPath $Build.OutputDir) {
        Remove-Item -LiteralPath $Build.OutputDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Build.OutputDir -Force | Out-Null

    $arguments = @(
        'publish'
        $projectPath
        '-c'
        $Configuration
        "-p:Platform=$($Build.Platform)"
        "-p:RuntimeIdentifier=$($Build.RuntimeIdentifier)"
        '-p:GenerateAppxPackageOnBuild=true'
        '-p:UapAppxPackageBuildMode=SideloadOnly'
        '-p:AppxBundle=Never'
        '-p:AppxPackageSigningEnabled=false'
        '-p:PackageCertificateThumbprint='
        "-p:AppxPackageDir=$appxPackageDir"
        "-p:ReleaseAppInstallerPath=$feedPath"
    )
    $profilePath = Join-Path $publishProfilesRoot $Build.PublishProfile
    if (Test-Path -LiteralPath $profilePath) {
        $arguments += "-p:PublishProfile=$profilePath"
    }
    else {
        # Publish profiles are gitignored; preserve their .NET deployment model on a fresh clone.
        $arguments += '-p:SelfContained=true'
    }

    Write-Host "Building GitHub MSIX for $($Build.Name)..."
    & dotnet @arguments

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($Build.Name)."
    }
}

function Invoke-MsixSigning {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileInfo[]]$Files
    )

    & $signCommand -Path $Files.FullName

    # Don't rely on the signing command's error reporting: confirm every package is signed.
    foreach ($file in $Files) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne 'Valid') {
            throw "$($file.Name) is not validly signed (status: $($signature.Status))."
        }
        Write-Host "  Verified signature on $($file.Name): $($signature.SignerCertificate.Subject)"
    }
}

function Get-RestoredPackageDirectory {
    param([string]$Id)

    $assetsPath = Join-Path $projectDir 'obj\project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $library = @($assets.libraries.PSObject.Properties | Where-Object {
        $_.Name.StartsWith("$Id/", [StringComparison]::OrdinalIgnoreCase)
    })
    if ($library.Count -ne 1) {
        throw "Expected one restored version of $Id in $assetsPath."
    }
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $path = Join-Path $folder ($library[0].Value.path -replace '/', '\')
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }
    throw "Restored package directory not found for $Id."
}

function Read-ZipXml {
    param([System.IO.Compression.ZipArchive]$Zip, [string]$Name)

    $entry = $Zip.GetEntry($Name)
    if (-not $entry) {
        throw "Archive is missing $Name."
    }
    $stream = $entry.Open()
    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.Load($stream)
        return $document
    }
    finally {
        $stream.Dispose()
    }
}

function Get-RuntimeDependencies {
    $runtimeRoot = Get-RestoredPackageDirectory 'Microsoft.WindowsAppSDK.Runtime'
    foreach ($build in $builds) {
        $runtimeDir = Join-Path $runtimeRoot "tools\MSIX\win10-$($build.Name)"
        $files = @(Get-ChildItem -LiteralPath $runtimeDir -Filter 'Microsoft.WindowsAppRuntime.*.msix' -File |
            Where-Object { $_.Name -notmatch '\.(Main|DDLM|Singleton)\b' })
        if ($files.Count -ne 1) {
            throw "Expected one Windows App Runtime framework package in $runtimeDir."
        }
        $zip = [System.IO.Compression.ZipFile]::OpenRead($files[0].FullName)
        try {
            $manifest = Read-ZipXml $zip 'AppxManifest.xml'
            $identity = $manifest.Package.Identity
            if ($manifest.Package.Properties.Framework -ne 'true' -or
                $identity.ProcessorArchitecture -ne $build.Name) {
                throw "Unexpected runtime framework identity in $($files[0].FullName)."
            }
            $name = "$($identity.Name)_$($identity.Version)_$($build.Name).msix"
            $destination = Join-Path $versionedOutputDir $name
            Copy-Item -LiteralPath $files[0].FullName -Destination $destination -Force
            [pscustomobject]@{
                Name = $identity.Name
                Publisher = $identity.Publisher
                Version = $identity.Version
                Architecture = $build.Name
                File = $destination
                Uri = "$releaseBase/$name"
            }
        }
        finally {
            $zip.Dispose()
        }
    }
}

function Write-AppInstallerFeed {
    $ns = 'http://schemas.microsoft.com/appx/appinstaller/2021'
    $document = [System.Xml.XmlDocument]::new()
    $root = $document.CreateElement('AppInstaller', $ns)
    $root.SetAttribute('Uri', $feedUri)
    $root.SetAttribute('Version', $version)
    $document.AppendChild($root) | Out-Null

    $bundle = $document.CreateElement('MainBundle', $ns)
    $bundle.SetAttribute('Name', $identity.Name)
    $bundle.SetAttribute('Publisher', $identity.Publisher)
    $bundle.SetAttribute('Version', $version)
    $bundle.SetAttribute('Uri', "$releaseBase/$bundleName")
    $root.AppendChild($bundle) | Out-Null

    if ($dependencies.Count -gt 0) {
        $dependencyList = $document.CreateElement('Dependencies', $ns)
        foreach ($dependency in $dependencies) {
            $package = $document.CreateElement('Package', $ns)
            foreach ($attribute in 'Name', 'Publisher', 'Version', 'Uri') {
                $package.SetAttribute($attribute, $dependency.$attribute)
            }
            $package.SetAttribute('ProcessorArchitecture', $dependency.Architecture)
            $dependencyList.AppendChild($package) | Out-Null
        }
        $root.AppendChild($dependencyList) | Out-Null
    }

    $updates = $document.CreateElement('UpdateSettings', $ns)
    $onLaunch = $document.CreateElement('OnLaunch', $ns)
    $onLaunch.SetAttribute('HoursBetweenUpdateChecks', '0')
    $onLaunch.SetAttribute('ShowPrompt', 'false')
    $onLaunch.SetAttribute('UpdateBlocksActivation', 'false')
    $updates.AppendChild($onLaunch) | Out-Null
    $updates.AppendChild($document.CreateElement('AutomaticBackgroundTask', $ns)) | Out-Null
    $root.AppendChild($updates) | Out-Null
    $document.Save($feedPath)
}

function Set-EmbeddedAppInstaller {
    param([System.Xml.XmlDocument]$Manifest)

    $ns = 'http://schemas.microsoft.com/appx/manifest/uap/windows10/13'
    $root = $Manifest.DocumentElement
    $root.SetAttribute('xmlns:uap13', $ns)
    $ignorable = @($root.GetAttribute('IgnorableNamespaces').Split(' ', [StringSplitOptions]::RemoveEmptyEntries)) + 'uap13'
    $root.SetAttribute('IgnorableNamespaces', (($ignorable | Select-Object -Unique) -join ' '))
    $properties = $Manifest.Package.Properties
    $autoUpdate = $properties.GetElementsByTagName('AutoUpdate', $ns) | Select-Object -First 1
    if (-not $autoUpdate) {
        $autoUpdate = $Manifest.CreateElement('uap13', 'AutoUpdate', $ns)
        $properties.AppendChild($autoUpdate) | Out-Null
    }
    $installer = $autoUpdate.GetElementsByTagName('AppInstaller', $ns) | Select-Object -First 1
    if (-not $installer) {
        $installer = $Manifest.CreateElement('uap13', 'AppInstaller', $ns)
        $autoUpdate.AppendChild($installer) | Out-Null
    }
    $installer.SetAttribute('File', 'Update.appinstaller')
    $Manifest.Save($manifestPath)
}

function Assert-ReleaseBundle {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($bundlePath)
    try {
        $manifest = Read-ZipXml $zip 'AppxMetadata/AppxBundleManifest.xml'
        if ($manifest.Bundle.Identity.Name -ne $identity.Name -or
            $manifest.Bundle.Identity.Publisher -ne $identity.Publisher -or
            $manifest.Bundle.Identity.Version -ne $version) {
            throw 'Bundle identity does not match the source manifest.'
        }
        $applications = @($manifest.Bundle.Packages.Package | Where-Object { $_.Type -eq 'application' })
        $architectures = @($applications | ForEach-Object { $_.Architecture } | Sort-Object)
        if ($applications.Count -ne 2 -or ($architectures -join ',') -ne 'arm64,x64') {
            throw 'The bundle must contain exactly one x64 and one ARM64 application package.'
        }
        $expectedFeed = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($feedPath))
        foreach ($application in $applications) {
            $entry = $zip.GetEntry($application.FileName)
            if (-not $entry) { throw "Bundle is missing $($application.FileName)." }
            $stream = $entry.Open()
            $memory = [System.IO.MemoryStream]::new()
            try {
                $stream.CopyTo($memory)
                $memory.Position = 0
                $inner = [System.IO.Compression.ZipArchive]::new($memory, [System.IO.Compression.ZipArchiveMode]::Read, $true)
                try {
                    $packageManifest = Read-ZipXml $inner 'AppxManifest.xml'
                    $packageIdentity = $packageManifest.Package.Identity
                    if ($packageIdentity.Name -ne $identity.Name -or
                        $packageIdentity.Publisher -ne $identity.Publisher -or
                        $packageIdentity.Version -ne $version -or
                        $packageIdentity.ProcessorArchitecture -ne $application.Architecture) {
                        throw "Unexpected inner package identity: $($application.FileName)."
                    }
                    $blockMap = Read-ZipXml $inner 'AppxBlockMap.xml'
                    if (-not ($blockMap.BlockMap.File | Where-Object { $_.Name -eq 'Update.appinstaller' })) {
                        throw "Block map does not include Update.appinstaller: $($application.FileName)."
                    }
                    $feedEntry = $inner.GetEntry('Update.appinstaller')
                    if (-not $feedEntry) { throw 'Inner package is missing Update.appinstaller.' }
                    $feedStream = $feedEntry.Open()
                    $feedMemory = [System.IO.MemoryStream]::new()
                    try {
                        $feedStream.CopyTo($feedMemory)
                        if ([Convert]::ToBase64String($feedMemory.ToArray()) -ne $expectedFeed) {
                            throw 'Embedded App Installer feed differs from the published feed.'
                        }
                    }
                    finally {
                        $feedStream.Dispose()
                        $feedMemory.Dispose()
                    }
                }
                finally { $inner.Dispose() }
            }
            finally {
                $stream.Dispose()
                $memory.Dispose()
            }
        }
    }
    finally { $zip.Dispose() }

    [xml]$feed = Get-Content -LiteralPath $feedPath -Raw
    $assets = @($bundlePath, $feedPath) + @($dependencies | ForEach-Object { $_.File })
    foreach ($uri in @($feed.AppInstaller.MainBundle.Uri) + @($dependencies | ForEach-Object { $_.Uri })) {
        $fileName = [Uri]::UnescapeDataString(([Uri]$uri).Segments[-1])
        if (-not ($assets | Where-Object { [System.IO.Path]::GetFileName($_) -eq $fileName -and (Test-Path -LiteralPath $_) })) {
            throw "Feed URI has no matching release asset: $uri"
        }
    }
}

$scriptFailed = $false
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The dotnet SDK is required but was not found on PATH.'
    }

    foreach ($requiredFile in $projectPath, $manifestPath) {
        if (-not (Test-Path -LiteralPath $requiredFile)) {
            throw "Required file not found: $requiredFile"
        }
    }

    if (-not $NoSign -and -not (Get-Command $signCommand -ErrorAction SilentlyContinue)) {
        throw "'$signCommand' was not found. It is defined in the maintainer's PowerShell profile; run this script from a pwsh session that loaded it."
    }

    $originalManifest = [System.IO.File]::ReadAllBytes($manifestPath)
    $manifest = [System.Xml.XmlDocument]::new()
    $manifest.PreserveWhitespace = $true
    $manifest.Load($manifestPath)
    $identity = $manifest.Package.Identity
    if (-not $identity.Version -or -not $identity.Name -or -not $identity.Publisher) {
        throw "Missing package identity in $manifestPath."
    }
    $version = $identity.Version
    $tag = Get-ReleaseTag -Version $version
    $versionedOutputDir = Join-Path $outputRoot "$version-gh"
    New-Item -ItemType Directory -Path $versionedOutputDir -Force | Out-Null

    $remote = & git -C $repoRoot remote get-url origin
    if ($LASTEXITCODE -ne 0 -or $remote -notmatch '(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)(?<repo>[^/]+/[^/]+?)(?:\.git)?/?$') {
        throw 'The origin remote must identify a GitHub repository.'
    }
    $repository = $Matches.repo
    $releaseBase = "https://github.com/$repository/releases/download/$tag"
    $bundleName = "${appName}_${version}.msixbundle"
    $bundlePath = Join-Path $versionedOutputDir $bundleName
    $feedPath = Join-Path $versionedOutputDir "$appName.appinstaller"
    $feedUri = "https://github.com/$repository/releases/latest/download/$appName.appinstaller"

    Write-Host "Project: $appName ($projectPath), version $version"

    # Check git/gh state before the (slow) build so problems surface immediately.
    if (-not $SkipRelease) {
        $branch = Assert-ReleaseReady -Tag $tag
        Write-Host "Releasing $tag from branch '$branch'."
    }

    & dotnet restore $projectPath -r win-x64 "-p:Configuration=$Configuration" -p:Platform=x64 --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet restore failed.'
    }

    $selfContained = & dotnet msbuild $projectPath -nologo "-p:Configuration=$Configuration" -p:Platform=x64 -getProperty:WindowsAppSDKSelfContained
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not evaluate WindowsAppSDKSelfContained.'
    }
    $dependencies = @()
    if (($selfContained -join '').Trim() -ne 'true') {
        $dependencies = @(Get-RuntimeDependencies)
    }
    Write-AppInstallerFeed

    # Both architectures embed the same feed. Never persist release-only manifest edits.
    try {
        Set-EmbeddedAppInstaller $manifest
        foreach ($build in $builds) {
            Invoke-GitHubMsixBuild -Build $build
        }
    }
    finally {
        [System.IO.File]::WriteAllBytes($manifestPath, $originalManifest)
    }

    $staging = Join-Path $versionedOutputDir 'bundle-input'
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Get-ChildItem -LiteralPath $staging -Filter '*.msix' -File | Remove-Item -Force
    foreach ($build in $builds) {
        # Exclude Dependencies\* (e.g. Microsoft.WindowsAppRuntime.2.msix) - only collect the app's own package.
        $msixFiles = @(Get-ChildItem -LiteralPath $build.OutputDir -Recurse -Filter '*.msix' -File |
            Where-Object { $_.FullName -notmatch '\\Dependencies\\' })
        if (-not $msixFiles) {
            throw "No .msix file was found under $($build.OutputDir) for $($build.Name)."
        }
        if ($msixFiles.Count -gt 1) {
            throw "Expected one app package under $($build.OutputDir) for $($build.Name), found: $(($msixFiles | ForEach-Object Name) -join ', ')."
        }
        foreach ($msix in $msixFiles) {
            $destination = Join-Path $staging $msix.Name
            Copy-Item -LiteralPath $msix.FullName -Destination $destination -Force
            Write-Host "  Staged $($msix.Name)"
        }
    }

    $makeAppx = Get-Command makeappx.exe -ErrorAction SilentlyContinue
    if ($makeAppx) {
        $makeAppxPath = $makeAppx.Source
    }
    else {
        $buildTools = Get-RestoredPackageDirectory 'Microsoft.Windows.SDK.BuildTools'
        $hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        $tool = Get-ChildItem -LiteralPath (Join-Path $buildTools 'bin') -Filter 'makeappx.exe' -File -Recurse |
            Where-Object { $_.Directory.Name -eq $hostArchitecture } | Select-Object -First 1
        if (-not $tool) { throw "makeappx.exe was not found for $hostArchitecture." }
        $makeAppxPath = $tool.FullName
    }
    & $makeAppxPath bundle /d $staging /p $bundlePath /bv $version /o
    if ($LASTEXITCODE -ne 0) {
        throw 'makeappx bundle failed.'
    }
    Assert-ReleaseBundle
    if (-not $NoSign) {
        Invoke-MsixSigning -Files @(Get-Item -LiteralPath $bundlePath)
    }
    $packages = @(Get-Item -LiteralPath (@($bundlePath, $feedPath) + @($dependencies | ForEach-Object { $_.File })))
    Write-Host "Verified bundle, feed and framework dependencies are under $versionedOutputDir"
    Write-Host "Install/update feed (after publishing a stable release): $feedUri"

    if (-not $SkipRelease) {
        $commit = (& git -C $repoRoot rev-parse HEAD).Trim()

        # Local-only tag; GitHub creates the remote tag at $commit when the draft is published.
        Write-Host "Creating local tag $tag at HEAD of '$branch' ($commit)..."
        & git -C $repoRoot tag $tag $commit
        if ($LASTEXITCODE -ne 0) {
            throw "git tag $tag failed."
        }

        # Always a draft: review and publish it manually on GitHub.
        $ghArguments = @('release', 'create', $tag) + $packages.FullName + @('--title', $tag, '--target', $commit, '--draft')
        $notesFile = $null
        if ($Notes) {
            # Pass notes via a file so quotes/newlines survive Windows PowerShell 5.1 native arg passing.
            $notesFile = [System.IO.Path]::GetTempFileName()
            [System.IO.File]::WriteAllText($notesFile, $Notes, [System.Text.UTF8Encoding]::new($false))
            $ghArguments += @('--notes-file', $notesFile)
        }
        else {
            $ghArguments += '--generate-notes'
        }
        if ($Prerelease) {
            $ghArguments += '--prerelease'
        }
        Write-Host "Creating draft GitHub release $tag..."
        Push-Location -LiteralPath $repoRoot
        try {
            & gh @ghArguments
            if ($LASTEXITCODE -ne 0) {
                throw "gh release create failed. Remove the local tag before retrying: git tag -d $tag"
            }
        }
        finally {
            Pop-Location
            if ($notesFile) {
                Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue
            }
        }

        Write-Host "Done. Draft release $tag is saved; publishing it on GitHub will create the tag at $commit."
    }
    else {
        Write-Host 'Done.'
    }
}
catch {
    $scriptFailed = $true
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
}

if ($scriptFailed) {
    exit 1
}

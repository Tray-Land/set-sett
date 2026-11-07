<#
.SYNOPSIS
  Builds Set Sett for the Microsoft Store and uploads it to a new draft submission.

.DESCRIPTION
  1. Reads the version and identity from Package.appxmanifest (the Store identity).
  2. Builds x64 and ARM64 packages with Visual Studio's MSBuild (the .NET SDK's MSIX tasks fail here).
  3. Bundles them with makeappx and checks the bundle (identity, version, no duplicate files).
  4. Wraps the bundle as an .msixupload in a subfolder, because msstore's WinUI publisher only picks up
     .msixupload/.appxupload/.msix files that sit ONE FOLDER BELOW the input directory.
  5. Runs `msstore publish`, which DELETES any pending submission for the app and creates a new draft.
     By default the draft is not committed: review it in Partner Center, then submit it there.

  Needs: Visual Studio with MSBuild, the Windows SDK (makeappx), and a configured msstore CLI.

.PARAMETER ProductId   Store product id (default: Set Sett).
.PARAMETER SkipBuild   Reuse the packages from the last build.
.PARAMETER NoUpload    Build and bundle only; do not touch the Store.
.PARAMETER Commit      Submit for certification instead of leaving a draft.
.PARAMETER Force       Do not ask before replacing the pending submission.
#>
[CmdletBinding()]
param(
    [string]$ProductId = '9N450BM817TQ',
    [switch]$SkipBuild,
    [switch]$NoUpload,
    [switch]$Commit,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'SetSett.csproj'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

# --- 1. Version and identity -------------------------------------------------------------------
Step 'Reading Package.appxmanifest'
[xml]$manifest = Get-Content (Join-Path $root 'Package.appxmanifest') -Raw
$identity = $manifest.Package.Identity
$version = $identity.Version
if ($version -notmatch '^\d+\.\d+\.\d+\.0$') { throw "Identity/@Version '$version' must have 0 as its fourth part for the Store." }

[xml]$csproj = Get-Content $project -Raw
$gitHubName = $csproj.SelectSingleNode('//GitHubIdentityName').InnerText
if ($identity.Name -eq $gitHubName -or $identity.Publisher -notmatch '^CN=[0-9A-Fa-f-]{36}$') {
    throw "The manifest does not carry the Store identity (Name=$($identity.Name), Publisher=$($identity.Publisher)). Is a GitHub release build half done? Try: git checkout Package.appxmanifest"
}
Write-Host "Version $version, identity $($identity.Name)"

# --- 2. Build ---------------------------------------------------------------------------------
function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        # Stable Visual Studio first, then a preview one.
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if (-not $found) { $found = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1 }
        if ($found) { return $found }
    }
    $found = Get-ChildItem "$env:ProgramFiles\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($found) { return $found.FullName }
    throw 'Visual Studio MSBuild was not found.'
}

if (-not $SkipBuild) {
    $msbuild = Find-MSBuild
    Write-Host "MSBuild: $msbuild"
    # One build per architecture: a single multi-platform build skipped ARM64 on some runs.
    # AppxBundle=Never keeps each package whole (all scales inside), so no resource packs are needed.
    # Symbols are off: the symbol-package step fails on this machine, and Store symbols only help crash analytics.
    foreach ($platform in 'x64', 'ARM64') {
        Step "Building $platform (Release, Store upload mode)"
        & $msbuild $project -restore -m -v:m -nologo `
            -p:Configuration=Release "-p:Platform=$platform" `
            -p:UapAppxPackageBuildMode=StoreUpload -p:AppxBundle=Never `
            -p:AppxSymbolPackageEnabled=false
        if ($LASTEXITCODE -ne 0) { throw "MSBuild failed for $platform (exit code $LASTEXITCODE)." }
    }
}

function Find-Package($platform, $rid) {
    $arch = $platform.ToLowerInvariant()
    $pattern = Join-Path $root "bin\$platform\Release\*\$rid\Upload\SetSett_${version}_$arch\SetSett_${version}_$arch.msix"
    $file = Get-ChildItem $pattern -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { throw "No $platform package for $version found ($pattern)." }
    Write-Host ("{0}: {1:N1} MB, built {2}" -f $platform, ($file.Length / 1MB), $file.LastWriteTime)
    return $file
}
Step 'Locating packages'
$x64 = Find-Package 'x64' 'win-x64'
$arm64 = Find-Package 'ARM64' 'win-arm64'

# --- 3. Bundle and check -----------------------------------------------------------------------
Step 'Bundling'
$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Directory.Parent.Name) } -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe (Windows SDK) was not found.' }

$work = Join-Path $root "AppPackages\Store-$version"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$stage = New-Item -ItemType Directory (Join-Path $work 'bundle-in')
Copy-Item $x64.FullName, $arm64.FullName $stage.FullName

# Two full packages and no resource packs. A full x64 package next to scale resource packs is a duplicate-resource error.
$bundle = Join-Path $work "SetSett_${version}_x64_arm64.msixbundle"
& $makeappx.FullName bundle /d $stage.FullName /p $bundle /bv $version /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx failed (exit code $LASTEXITCODE)." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($bundle)
try {
    $entry = $zip.GetEntry('AppxMetadata/AppxBundleManifest.xml')
    $reader = New-Object IO.StreamReader($entry.Open())
    [xml]$bundleManifest = $reader.ReadToEnd(); $reader.Close()
} finally { $zip.Dispose() }
$bi = $bundleManifest.Bundle.Identity
if ($bi.Name -ne $identity.Name -or $bi.Publisher -ne $identity.Publisher -or $bi.Version -ne $version) {
    throw "Bundle identity ($($bi.Name), $($bi.Publisher), $($bi.Version)) does not match the manifest."
}
$parts = @($bundleManifest.Bundle.Packages.Package)
if ($parts.Count -ne 2 -or ($parts | Where-Object Type -ne 'application')) { throw 'Expected exactly the x64 and ARM64 application packages in the bundle.' }
foreach ($file in $stage.GetFiles()) {
    $z = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $dups = @($z.Entries | Group-Object { $_.FullName.ToLowerInvariant() } | Where-Object Count -gt 1)
        if ($dups.Count) { throw "$($file.Name) has duplicate entries, e.g. $($dups[0].Name)." }
    } finally { $z.Dispose() }
}
Write-Host ("Bundle OK: {0} MB, architectures {1}" -f [math]::Round((Get-Item $bundle).Length / 1MB, 1), (($parts.Architecture) -join ', '))

# --- 4. Wrap as .msixupload one folder down ----------------------------------------------------
Step 'Creating the .msixupload'
$uploadRoot = New-Item -ItemType Directory (Join-Path $work 'upload')
$uploadDir = New-Item -ItemType Directory (Join-Path $uploadRoot.FullName "SetSett_$version")
$tmpZip = Join-Path $uploadDir.FullName 'bundle.zip'
Compress-Archive -Path $bundle -DestinationPath $tmpZip -Force
$upload = Join-Path $uploadDir.FullName "SetSett_${version}_x64_arm64_bundle.msixupload"
Move-Item $tmpZip $upload
Write-Host $upload

if ($NoUpload) {
    Write-Host "`n-NoUpload: stopped before the Store. Package is at $upload" -ForegroundColor Yellow
    return
}

# --- 5. Create the submission and upload -------------------------------------------------------
Step 'Publishing with msstore'
if (-not (Get-Command msstore -ErrorAction SilentlyContinue)) { throw 'msstore CLI not found on PATH.' }
$what = if ($Commit) { 'create a new submission and SUBMIT it for certification' } else { 'create a new draft submission (not submitted)' }
if (-not $Force -and -not $PSCmdlet.ShouldContinue("This deletes any pending submission for $ProductId and will $what.`nUpload $version?", 'Replace the pending submission')) {
    Write-Host 'Cancelled.'
    return
}

$publishArgs = @($root, '-i', $uploadRoot.FullName, '-id', $ProductId, '-ut', '600')
if (-not $Commit) { $publishArgs += '--noCommit' }
& msstore publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "msstore publish failed (exit code $LASTEXITCODE)." }

Step 'Submission status'
& msstore submission status $ProductId
Write-Host "`nDone. Check the draft's Packages page in Partner Center for $version and any validation errors, add release notes, then submit." -ForegroundColor Green

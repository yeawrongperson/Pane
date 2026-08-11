[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $repoRoot 'Wallflow.slnx'
$appProject = Join-Path $repoRoot 'src\Wallflow\Wallflow.csproj'
$testProject = Join-Path $repoRoot 'tests\Wallflow.Core.Tests\Wallflow.Core.Tests.csproj'
$versionProps = Join-Path $repoRoot 'Directory.Build.props'
$releaseRoot = Join-Path $repoRoot 'artifacts\releases'
$publishRoot = Join-Path $repoRoot 'artifacts\publish'
$smokeProcess = $null

function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    Write-Host "`n> $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code ${LASTEXITCODE}: $FilePath" }
}

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe generated path outside $Parent`: $fullPath"
    }
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return '{0:N2} GB' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N2} MB' -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return '{0:N2} KB' -f ($Bytes / 1KB) }
    return "$Bytes bytes"
}

function Wait-FileReadable([string]$Path, [int]$TimeoutSeconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'Read')
            $stream.Dispose()
            return
        }
        catch [System.IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 500
        }
    } while ($true)
}

function Get-PeMachine([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset + 4
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Test-FileContainsTextMarker([string]$Path, [string]$Marker) {
    if ([string]::IsNullOrEmpty($Marker)) { return $false }

    $latin1 = [System.Text.Encoding]::GetEncoding(28591)
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $patterns = @(
        $latin1.GetString($utf8.GetBytes($Marker)),
        $latin1.GetString([System.Text.Encoding]::Unicode.GetBytes($Marker))
    )
    $maximumPatternLength = ($patterns | Measure-Object -Property Length -Maximum).Maximum
    $buffer = [byte[]]::new(1MB)
    $tail = ''
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $chunk = $tail + $latin1.GetString($buffer, 0, $read)
            foreach ($pattern in $patterns) {
                if ($chunk.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
            }
            $tailLength = [Math]::Min($maximumPatternLength - 1, $chunk.Length)
            $tail = if ($tailLength -gt 0) { $chunk.Substring($chunk.Length - $tailLength) } else { '' }
        }
        return $false
    }
    finally { $stream.Dispose() }
}

function Assert-NoPrivateBuildPaths([string]$ExecutablePath, [string]$RepositoryRoot) {
    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $markers = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($marker in @($RepositoryRoot, $userProfile, 'C:\Users\', 'Documents\ChatGPT\Pane')) {
        if ([string]::IsNullOrWhiteSpace($marker)) { continue }
        [void]$markers.Add($marker.TrimEnd('\', '/'))
        [void]$markers.Add($marker.Replace('\', '/').TrimEnd('/'))
    }

    foreach ($marker in $markers) {
        if (Test-FileContainsTextMarker $ExecutablePath $marker) {
            throw "Private developer-machine path marker found in Pane.exe: $marker"
        }
    }
}

function Test-EmbeddedIcon([string]$ExecutablePath, [string]$IconPath) {
    Add-Type -AssemblyName System.Drawing
    $executableIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($ExecutablePath)
    $expectedIcon = [System.Drawing.Icon]::new($IconPath, 32, 32)
    $actualBitmap = $executableIcon.ToBitmap()
    $expectedBitmap = $expectedIcon.ToBitmap()
    try {
        if ($actualBitmap.Size -ne $expectedBitmap.Size) { return $false }
        for ($y = 0; $y -lt $actualBitmap.Height; $y++) {
            for ($x = 0; $x -lt $actualBitmap.Width; $x++) {
                if ($actualBitmap.GetPixel($x, $y).ToArgb() -ne $expectedBitmap.GetPixel($x, $y).ToArgb()) { return $false }
            }
        }
        return $true
    }
    finally {
        $actualBitmap.Dispose()
        $expectedBitmap.Dispose()
        $executableIcon.Dispose()
        $expectedIcon.Dispose()
    }
}

try {
    Write-Host '========================================' -ForegroundColor Cyan
    Write-Host 'Pane Portable Release' -ForegroundColor Cyan
    Write-Host '========================================' -ForegroundColor Cyan

    if (Get-Process Pane -ErrorAction SilentlyContinue) {
        throw 'Pane is running and may lock release files. Exit Pane from its notification-area menu, then run the release again.'
    }

    if (-not $Version) {
        [xml]$props = Get-Content -Raw -LiteralPath $versionProps
        $prefix = [string]$props.Project.PropertyGroup.VersionPrefix
        $suffix = [string]$props.Project.PropertyGroup.VersionSuffix
        if (-not $prefix) { throw 'Directory.Build.props does not define VersionPrefix.' }
        $Version = if ($suffix) { "$prefix-$suffix" } else { $prefix }
    }
    if ($Version -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<suffix>[0-9A-Za-z.-]+))?$') {
        throw "Invalid version: $Version"
    }
    $numericVersion = "$($Matches.major).$($Matches.minor).$($Matches.patch).0"
    $releaseName = "Pane-$Version-win-x64"
    $releaseDirectory = Join-Path $releaseRoot $releaseName
    $publishDirectory = Join-Path $publishRoot $releaseName
    $zipPath = Join-Path $releaseRoot "$releaseName.zip"
    $checksumPath = "$zipPath.sha256"
    $releaseNotesPath = Join-Path $releaseRoot "Pane-$Version-release-notes.md"

    foreach ($path in @($releaseDirectory, $publishDirectory, $zipPath, $checksumPath, $releaseNotesPath)) {
        Assert-ChildPath $path (Join-Path $repoRoot 'artifacts')
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    New-Item -ItemType Directory -Force -Path $releaseDirectory, $publishDirectory | Out-Null

    $gitStatus = & git -C $repoRoot status --short
    if ($gitStatus) { Write-Warning 'The repository has uncommitted changes. They are preserved and included in this build.' }

    $sourcePathFindings = Get-ChildItem (Join-Path $repoRoot 'src') -Recurse -File -Include '*.cs','*.xaml','*.csproj','*.props','*.targets','*.manifest' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Select-String -Pattern 'C:\\Users\\|Documents\\ChatGPT|\\Pane\\src\\|bin\\Debug' -SimpleMatch:$false
    if ($sourcePathFindings) { throw "Machine-specific absolute path found in application source/config: $($sourcePathFindings[0].Path):$($sourcePathFindings[0].LineNumber)" }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Build Tools locator (vswhere.exe) was not found.' }
    $vsPath = (& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1)
    if (-not $vsPath) { throw 'Visual Studio 2022 with MSBuild was not found.' }
    $msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
    if (-not (Test-Path -LiteralPath $msbuild)) { throw "MSBuild was not found at $msbuild" }
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-home'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

    Invoke-Native $dotnet @('restore', $solution, '-p:PaneOfficialReleaseBuild=true')
    Invoke-Native $dotnet @('restore', $appProject, '-r', 'win-x64', '-p:PaneOfficialReleaseBuild=true', '-p:SelfContained=true', '-p:WindowsAppSDKSelfContained=true', '-p:PublishSingleFile=true', '-p:IncludeAllContentForSelfExtract=true', '-p:EnableMsixTooling=true')
    Invoke-Native $msbuild @($solution, '/restore:false', '/p:Configuration=Release', '/p:Platform=x64', '/p:PaneOfficialReleaseBuild=true', "/p:Version=$Version", "/p:AssemblyVersion=$numericVersion", "/p:FileVersion=$numericVersion", "/p:InformationalVersion=$Version", '/v:minimal')
    Invoke-Native $dotnet @('test', $testProject, '-c', 'Release', '--no-build', '--no-restore', '-p:Platform=x64')
    Invoke-Native $msbuild @($appProject, '/t:Publish', '/restore:false', '/p:Configuration=Release', '/p:Platform=x64', '/p:PaneOfficialReleaseBuild=true', '/p:PublishProfile=win-x64', "/p:PublishDir=$publishDirectory\", "/p:Version=$Version", "/p:AssemblyVersion=$numericVersion", "/p:FileVersion=$numericVersion", "/p:InformationalVersion=$Version", '/v:minimal')

    $publishedExe = Join-Path $publishDirectory 'Pane.exe'
    if (-not (Test-Path -LiteralPath $publishedExe)) { throw "Publish did not produce Pane.exe at $publishedExe" }
    if ((Get-Item -LiteralPath $publishedExe).Length -le 0) { throw 'Published Pane.exe is empty.' }

    $publishItems = Get-ChildItem -LiteralPath $publishDirectory -Force | Where-Object { $_.Extension -notin '.pdb','.xml' }
    foreach ($publishItem in $publishItems) {
        Copy-Item -LiteralPath $publishItem.FullName -Destination $releaseDirectory -Recurse -Force
    }
    Get-ChildItem -LiteralPath $releaseDirectory -Recurse -File |
        Where-Object { $_.Extension -in '.pdb','.xml' } |
        Remove-Item -Force

    $suspicious = Get-ChildItem -LiteralPath $releaseDirectory -Recurse -Force | Where-Object {
        $_.Name -match '\.(cs|csproj|sln|slnx|user|pdb)$' -or $_.Name -in '.git','obj','tests'
    }
    if ($suspicious) { throw "Suspicious development file in release: $($suspicious[0].FullName)" }
    if (Get-ChildItem -LiteralPath $releaseDirectory -Recurse -File | Where-Object Name -like '*Tests*.dll') {
        throw 'A test assembly was found in the release directory.'
    }

    $releaseExe = Join-Path $releaseDirectory 'Pane.exe'
    if (-not (Test-Path -LiteralPath $releaseExe)) { throw "Release staging did not contain Pane.exe at $releaseExe" }
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($releaseExe)
    if ($versionInfo.ProductName -ne 'Pane' -or $versionInfo.FileDescription -ne 'Pane') {
        throw "Pane.exe metadata is incorrect (Product='$($versionInfo.ProductName)', Description='$($versionInfo.FileDescription)')."
    }
    if ((Get-PeMachine $releaseExe) -ne 0x8664) { throw 'Pane.exe is not an x64 executable.' }
    Assert-NoPrivateBuildPaths $releaseExe $repoRoot
    $paneIcon = Join-Path $repoRoot 'src\Wallflow\Assets\Pane.ico'
    if (-not (Test-EmbeddedIcon $releaseExe $paneIcon)) { throw 'Pane.exe does not contain the expected Pane icon.' }

    $smokeProcess = Start-Process -FilePath $releaseExe -WorkingDirectory $releaseDirectory -PassThru
    $smokeReady = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        $smokeProcess.Refresh()
        if ($smokeProcess.HasExited) { throw "Pane smoke test exited early with code $($smokeProcess.ExitCode)." }
        if ($smokeProcess.MainWindowHandle -ne 0) { $smokeReady = $true; break }
    }
    if (-not $smokeReady) { throw 'Pane smoke test did not create a window within 20 seconds.' }
    Stop-Process -Id $smokeProcess.Id -Force
    $smokeProcess.WaitForExit(5000) | Out-Null
    $smokeProcess.Dispose()
    $smokeProcess = $null
    Wait-FileReadable $releaseExe

    @"
# Pane $Version

Early beta release of Pane.

## Included
- Per-monitor static wallpaper configuration
- Independent per-monitor slideshows
- Monitor detection and topology visualization
- Persisted display profiles
- Notification-area background operation

## Requirements
- Windows 10 or Windows 11
- x64 PC

## Notes
This is an unsigned early beta. Windows SmartScreen may show an unknown-publisher warning.
"@ | Set-Content -LiteralPath $releaseNotesPath -Encoding utf8

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archiveCreated = $false
    for ($archiveAttempt = 1; $archiveAttempt -le 3; $archiveAttempt++) {
        try {
            if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
            [System.IO.Compression.ZipFile]::CreateFromDirectory(
                $releaseDirectory,
                $zipPath,
                [System.IO.Compression.CompressionLevel]::Optimal,
                $false
            )
            $archiveCreated = $true
            break
        }
        catch [System.IO.IOException] {
            if ($archiveAttempt -eq 3) { throw }
            Start-Sleep -Seconds 2
            Wait-FileReadable $releaseExe
        }
    }
    if (-not $archiveCreated) { throw 'ZIP creation failed.' }
    if (-not (Test-Path -LiteralPath $zipPath)) { throw 'ZIP creation failed.' }
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object FullName)
        if ($entryNames -notcontains 'Pane.exe') { throw 'ZIP does not contain Pane.exe at its root.' }
        if ($entryNames | Where-Object { $_ -match '(^|/)(src|tests|obj|\.git)(/|$)' -or $_ -match '\.(cs|csproj|sln|slnx|pdb)$' }) {
            throw 'ZIP contains a source, test, build, or symbol file.'
        }
    } finally { $archive.Dispose() }

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath $checksumPath -Encoding ascii
    $shippingFiles = Get-ChildItem -LiteralPath $releaseDirectory -Recurse -File | Sort-Object FullName
    $folderSize = ($shippingFiles | Measure-Object Length -Sum).Sum
    $exeSize = (Get-Item -LiteralPath $releaseExe).Length
    $zipSize = (Get-Item -LiteralPath $zipPath).Length

    Write-Host "`nShipping files:" -ForegroundColor Cyan
    foreach ($file in $shippingFiles) { Write-Host $file.FullName.Substring($releaseDirectory.Length + 1) }
    Write-Host "`n========================================" -ForegroundColor Green
    Write-Host 'Pane Release Complete' -ForegroundColor Green
    Write-Host '========================================' -ForegroundColor Green
    Write-Host "Version: $Version"
    Write-Host 'Platform: win-x64'
    Write-Host 'Configuration: Release'
    Write-Host "Release folder: $releaseDirectory"
    Write-Host "ZIP: $zipPath"
    Write-Host "Checksum: $checksumPath"
    Write-Host "SHA-256: $hash"
    Write-Host "Pane.exe: $(Format-Size $exeSize)"
    Write-Host "Release folder: $(Format-Size $folderSize)"
    Write-Host "ZIP: $(Format-Size $zipSize)"
    Write-Host 'BUILD: PASS'
    Write-Host 'TESTS: PASS'
    Write-Host 'PUBLISH: PASS'
    Write-Host 'SMOKE TEST: PASS'
    Write-Host 'PACKAGE: PASS'
    Write-Host '========================================' -ForegroundColor Green
}
catch {
    if ($smokeProcess -and -not $smokeProcess.HasExited) { Stop-Process -Id $smokeProcess.Id -Force -ErrorAction SilentlyContinue }
    Write-Host "`n========================================" -ForegroundColor Red
    Write-Host 'RELEASE ABORTED' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host '========================================' -ForegroundColor Red
    exit 1
}

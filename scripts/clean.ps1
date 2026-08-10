[CmdletBinding()]
param([switch]$IncludeBuildOutputs)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$safeTargets = [System.Collections.Generic.List[string]]::new()
$safeTargets.Add((Join-Path $repoRoot 'artifacts'))

if ($IncludeBuildOutputs) {
    $safeTargets.Add((Join-Path $repoRoot 'src\Wallflow\bin'))
    $safeTargets.Add((Join-Path $repoRoot 'src\Wallflow\obj'))
    $safeTargets.Add((Join-Path $repoRoot 'src\Wallflow.Core\bin'))
    $safeTargets.Add((Join-Path $repoRoot 'src\Wallflow.Core\obj'))
    $safeTargets.Add((Join-Path $repoRoot 'tests\Wallflow.Core.Tests\bin'))
    $safeTargets.Add((Join-Path $repoRoot 'tests\Wallflow.Core.Tests\obj'))
}

foreach ($target in $safeTargets) {
    $fullTarget = [System.IO.Path]::GetFullPath($target)
    if (-not $fullTarget.StartsWith($repoRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing unsafe clean target: $fullTarget"
    }
    if (Test-Path -LiteralPath $fullTarget) {
        Remove-Item -LiteralPath $fullTarget -Recurse -Force
        Write-Host "Removed $fullTarget"
    }
}

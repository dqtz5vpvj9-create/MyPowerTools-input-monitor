[CmdletBinding()]
param(
    [string] $MyPowerToolsRepoRoot,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $PrebuiltCliPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = if ([string]::IsNullOrWhiteSpace($MyPowerToolsRepoRoot)) {
    [System.IO.Path]::GetFullPath((Join-Path $toolRoot '..\..'))
} else {
    [System.IO.Path]::GetFullPath($MyPowerToolsRepoRoot)
}
$projectPath = Join-Path $toolRoot 'current-integration\src\InputMonitor.MyPowerTools\InputMonitor.MyPowerTools.csproj'
$surfaceProjectPath = Join-Path $toolRoot 'current-integration\src\InputMonitor.Surface\InputMonitor.Surface.csproj'
$modulePackageRoot = Join-Path $toolRoot 'current-integration\modules\input-monitor'
$surfacePackageRoot = Join-Path $modulePackageRoot 'ui\surface'
$repositoryModuleRoot = Join-Path $repoRoot 'modules\input-monitor'
$artifactsRoot = Join-Path $toolRoot 'artifacts'
$artifactPackage = Join-Path $artifactsRoot 'package'
$cliProject = Join-Path $repoRoot 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj'

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'src\MyPowerTools.Abstractions\MyPowerTools.Abstractions.csproj') -PathType Leaf)) {
    throw "MyPowerToolsRepoRoot '$repoRoot' is invalid."
}

# Only reuse a CLI explicitly supplied by the caller; standalone builds retain their build behavior.
$cliExecutable = $null
$cliInvocationArguments = @('run', '--project', $cliProject, '--configuration', $Configuration, '--')
if ($PSBoundParameters.ContainsKey('PrebuiltCliPath')) {
    if ([string]::IsNullOrWhiteSpace($PrebuiltCliPath) -or
        -not (Test-Path -LiteralPath $PrebuiltCliPath -PathType Leaf)) {
        throw "Prebuilt CLI '$PrebuiltCliPath' is missing or is not a file."
    }
    $cliExecutable = (Resolve-Path -LiteralPath $PrebuiltCliPath -ErrorAction Stop).ProviderPath
    $cliInvocationArguments = @()
}

$dotnet = Get-Command 'dotnet' -CommandType Application -ErrorAction Stop
if ($null -eq $cliExecutable) {
    $cliExecutable = $dotnet.Source
}
$dotnetArguments = @(
    'build'
    $projectPath
    '--configuration'
    $Configuration
    '--nologo'
    "-p:MyPowerToolsRepoRoot=$repoRoot"
)
& $dotnet.Source @dotnetArguments
$dotnetExitCode = $LASTEXITCODE
if ($dotnetExitCode -ne 0) {
    throw "dotnet build failed with exit code $dotnetExitCode."
}

$surfaceOutput = Join-Path $artifactsRoot 'surface'
$surfaceArguments = @(
    'build'
    $surfaceProjectPath
    '--configuration'
    $Configuration
    '--nologo'
    '--output'
    $surfaceOutput
    "-p:MyPowerToolsRepoRoot=$repoRoot"
)
& $dotnet.Source @surfaceArguments
$surfaceExitCode = $LASTEXITCODE
if ($surfaceExitCode -ne 0) {
    throw "dotnet surface build failed with exit code $surfaceExitCode."
}

New-Item -ItemType Directory -Path $surfacePackageRoot -Force | Out-Null
foreach ($extension in @('*.dll', '*.pdb', '*.deps.json')) {
    Get-ChildItem -LiteralPath $surfaceOutput -File -Filter $extension |
        Copy-Item -Destination $surfacePackageRoot -Force
}

function Remove-NonWindowsSqliteRuntimes {
    param([Parameter(Mandatory = $true)][string] $PackageRoot)

    $runtimesRoot = Join-Path $PackageRoot 'runtimes'
    if (-not (Test-Path -LiteralPath $runtimesRoot -PathType Container)) {
        return
    }

    Get-ChildItem -LiteralPath $runtimesRoot -Directory |
        Where-Object { $_.Name -notlike 'win-*' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
}

Remove-NonWindowsSqliteRuntimes -PackageRoot $modulePackageRoot

New-Item -ItemType Directory -Path $repositoryModuleRoot -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $modulePackageRoot -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $repositoryModuleRoot -Recurse -Force
}
Remove-NonWindowsSqliteRuntimes -PackageRoot $repositoryModuleRoot

if (Test-Path -LiteralPath $artifactPackage) {
    $resolvedTarget = [System.IO.Path]::GetFullPath($artifactPackage)
    $allowedPrefix = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTarget.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove artifact path outside '$artifactsRoot'."
    }
    Remove-Item -LiteralPath $artifactPackage -Recurse -Force
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Copy-Item -LiteralPath $modulePackageRoot -Destination $artifactPackage -Recurse -Force

$expectedAssembly = Join-Path $artifactPackage 'InputMonitor.MyPowerTools.dll'
if (-not (Test-Path -LiteralPath $expectedAssembly -PathType Leaf)) {
    throw "Expected adapter assembly '$expectedAssembly' is missing."
}

foreach ($runtimeFile in @('InputMonitor.MyPowerTools.deps.json', 'InputMonitor.Core.dll', 'Microsoft.Data.Sqlite.dll')) {
    $runtimePath = Join-Path $artifactPackage $runtimeFile
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        throw "Expected Input Monitor runtime dependency '$runtimePath' is missing."
    }
}

$sqliteNative = @(Get-ChildItem -LiteralPath (Join-Path $artifactPackage 'runtimes') -Filter 'e_sqlite3.dll' -Recurse -File -ErrorAction SilentlyContinue)
if ($sqliteNative.Count -eq 0) {
    throw "Expected SQLite native library under '$artifactPackage\runtimes'."
}

$expectedSurfaceAssembly = Join-Path $artifactPackage 'ui\surface\InputMonitor.Surface.dll'
if (-not (Test-Path -LiteralPath $expectedSurfaceAssembly -PathType Leaf)) {
    throw "Expected Surface assembly '$expectedSurfaceAssembly' is missing."
}

$cliArguments = $cliInvocationArguments + @(
    'package'
    'hash'
    $artifactPackage
)
& $cliExecutable @cliArguments
if ($LASTEXITCODE -ne 0) {
    throw "Failed to write package hashes for Input Monitor."
}

$signArguments = $cliInvocationArguments + @(
    'package'
    'sign-local'
    $artifactPackage
)
& $cliExecutable @signArguments
if ($LASTEXITCODE -ne 0) {
    throw "Failed to write the local signature hook for Input Monitor."
}

$sharedRoot = Join-Path $artifactPackage 'shared'
if (Test-Path -LiteralPath $sharedRoot -PathType Container) {
    foreach ($destination in @($modulePackageRoot, $repositoryModuleRoot)) {
        $sharedDestination = Join-Path $destination 'shared'
        New-Item -ItemType Directory -Path $sharedDestination -Force | Out-Null
        Get-ChildItem -LiteralPath $sharedRoot -Force |
            Copy-Item -Destination $sharedDestination -Force
    }
}

Write-Output "Release package staged at $artifactPackage"

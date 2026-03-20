<#
.SYNOPSIS
    Run TinyOS benchmark projects (.NET 6 and .NET 10), then build a comparison report.
.DESCRIPTION
    This script executes both benchmark projects using the same bench-programs data,
    captures BenchmarkDotNet output logs, extracts key timing and status values,
    and produces a CSV report in BenchmarkResults\TinyOSBenchmarkComparison.csv.
.NOTES
    - Ensure the repo is built first (dotnet build -c Release) before running.
    - Requires PowerShell 7+ for compatibility with -- and tee.
#>

param(
    [string]$RepoRoot = (Resolve-Path ".\" ).ProviderPath,
    [string]$Bench6Project = "benchmarks\TinyOSCore.Net6.Benchmarks\TinyOSCore.Net6.Benchmarks.csproj",
    [string]$Bench10Project = "benchmarks\TinyOSCore.Net10.Benchmarks\TinyOSCore.Net10.Benchmarks.csproj",
    [string]$ResultRoot = "BenchmarkResults",
    [int]$TimeoutSeconds = 15
)

function Invoke-ProjectBenchmark {
    param(
        [string]$projectPath,
        [string]$framework,
        [string]$outputDir
    )

    Write-Host "Running benchmark $projectPath framework=$framework" -ForegroundColor Cyan
    $fullProj = Join-Path $RepoRoot $projectPath
    $logFile = Join-Path $outputDir "$(Split-Path $projectPath -Leaf)-$framework.log"

    if (-not (Test-Path $outputDir)) {
        New-Item -Path $outputDir -ItemType Directory | Out-Null
    }

    $cmd = "dotnet run -c Release --framework $framework --project `"$fullProj`" -- --job Short --filter 'Benchmarks.RunTinyOSProgram*'"
    Write-Host "Command: $cmd"

    & pwsh -NoLogo -NoProfile -Command {
        param($cmd, $logFile)
        Invoke-Expression $cmd 2>&1 | Tee-Object $logFile
    } -ArgumentList $cmd, $logFile

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Benchmark run for $projectPath $framework returned exit code $LASTEXITCODE"
    }

    return $logFile
}

function Parse-BenchmarkLog {
    param(
        [string]$logFile,
        [string]$framework
    )

    $results = @()
    $current = $null

    foreach ($line in Get-Content $logFile) {
        if ($line -match 'Benchmark: .*\[SampleProgramFile=([^\]]+)\]') {
            $current = [pscustomobject]@{
                Framework        = $framework
                Case             = $matches[1]
                MeanMs           = $null
                MinMs            = $null
                MaxMs            = $null
                StdDevMs         = $null
                OOM              = $false
                TimedOut         = $false
                WarningFlags     = ''
                Logs             = ''
            }
            $results += $current
            continue
        }

        if ($current -eq $null) { continue }

        if ($line -match 'No Workload Results were obtained') { $current.OOM = $true }
        if ($line -match 'Benchmark timeout after') { $current.TimedOut = $true }
        if ($line -match 'Mean = ([0-9.]+) ms') { $current.MeanMs = [double]$matches[1] }
        if ($line -match 'Min = ([0-9.]+) ms') { $current.MinMs = [double]$matches[1] }
        if ($line -match 'Max = ([0-9.]+) ms') { $current.MaxMs = [double]$matches[1] }
        if ($line -match 'StdDev = ([0-9.]+) ms') { $current.StdDevMs = [double]$matches[1] }
        if ($line -match 'MinIterationTime') { $current.WarningFlags += 'MinIterationTime;' }

        $current.Logs += $line + "`n"
    }

    return $results
}

# ensure result path
$resultsFolder = Join-Path $RepoRoot $ResultRoot
if (-not (Test-Path $resultsFolder)) { New-Item -Path $resultsFolder -ItemType Directory | Out-Null }

Push-Location $RepoRoot
dotnet build -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$log6  = Invoke-ProjectBenchmark -projectPath $Bench6Project -framework 'net6.0'  -outputDir (Join-Path $resultsFolder 'Net6')
$log10 = Invoke-ProjectBenchmark -projectPath $Bench10Project -framework 'net10.0' -outputDir (Join-Path $resultsFolder 'Net10')
Pop-Location

$benchResults = @()
$benchResults += Parse-BenchmarkLog -logFile $log6 -framework 'net6.0'
$benchResults += Parse-BenchmarkLog -logFile $log10 -framework 'net10.0'

$csvOut = Join-Path $resultsFolder 'TinyOSBenchmarkComparison.csv'
$benchResults | Select-Object Framework, Case, MeanMs, MinMs, MaxMs, StdDevMs, OOM, TimedOut, WarningFlags | Export-Csv -NoTypeInformation -Path $csvOut

Write-Host "Benchmark comparison saved to $csvOut" -ForegroundColor Green

$benchResults | Sort-Object Case, Framework | Format-Table -AutoSize

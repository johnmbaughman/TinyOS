$sampleDir = Join-Path -Path $PSScriptRoot -ChildPath 'Sample Programs'
$benchListFile = Join-Path -Path $sampleDir -ChildPath 'bench-programs.txt'

if (-not (Test-Path $benchListFile)) {
    Write-Error "bench-programs.txt not found in $sampleDir"
    exit 1
}

$benchFiles = Get-Content $benchListFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
$missing = @()
foreach ($file in $benchFiles) {
    if (-not (Test-Path (Join-Path $sampleDir $file))) {
        $missing += $file
    }
}
if ($missing.Count -gt 0) {
    Write-Error "Missing bench program files: $($missing -join ', ')"
    exit 1
}

$issues = @()
foreach ($file in $benchFiles) {
    $regs = @{}
    1..10 | ForEach-Object { $regs['r' + $_] = $null }
    $lines = Get-Content (Join-Path $sampleDir $file)

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $raw = $lines[$i]
        $line = ($raw -split ';')[0].Trim()
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        $parts = $line -split '\s+' | Where-Object { $_ -ne '' }
        if ($parts.Count -eq 0) { continue }

        $op = $parts[0]
        switch ($op) {
            '6' { # Movi
                if ($parts.Count -ge 3) {
                    $r = $parts[1]
                    $v = $parts[2]
                    if ($v -match '^\$(\-?\d+)$') {
                        $regs[$r] = [int]$matches[1]
                    } else {
                        $regs[$r] = $null
                    }
                }
            }
            '7' { # Movr
                if ($parts.Count -ge 3) {
                    $r = $parts[1]; $src = $parts[2]
                    $regs[$r] = $regs[$src]
                }
            }
            '25' { # Sleep
                if ($parts.Count -ge 2) {
                    $r = $parts[1]
                    if ($regs[$r] -eq 0) {
                        $issues += [PSCustomObject]@{ File = $file; Line = $i + 1; Point = $line }
                    }
                }
            }
            default {
                # that might affect register by param (some ops do)
                if ($parts.Count -ge 2 -and $parts[1] -match '^r\d+$') {
                    # non-const set unknown
                    $regs[$parts[1]] = $null
                }
            }
        }
    }
}

if ($issues.Count -gt 0) {
    Write-Host "=== Infinite Sleep (sleep(0)) conditions detected in bench programs ===" -ForegroundColor Red
    $issues | Format-Table -AutoSize
    exit 1
}

Write-Host 'validate-bench-programs.ps1 passed: no infinite-sleep bench programs detected.' -ForegroundColor Green
exit 0

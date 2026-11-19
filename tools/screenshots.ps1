# Gera os prints do README (docs/*.png) com uma configuração de demonstração:
# perfil Padrão, sem lâmpada, sem nomes de dispositivos, IP, Device ID ou caminhos pessoais.
#   .\tools\screenshots.ps1
param([string]$Exe)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Exe) {
    dotnet build "$root\src\ClaudeColor.App" -c Debug -v quiet | Out-Null
    $Exe = Get-ChildItem "$root\src\ClaudeColor.App\bin\Debug" -Recurse -Filter ClaudeColor.exe |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}

$home_ = Join-Path ([IO.Path]::GetTempPath()) "claude-color-demo-$([guid]::NewGuid().ToString('N'))"
$shots = Join-Path $home_ 'shots'
New-Item -ItemType Directory -Force $home_ | Out-Null
@{ signalsEnabled = $true; theme = 'light'; lamp = @{ enabled = $false } } | ConvertTo-Json | Set-Content "$home_\config.json" -Encoding utf8

$env:CLAUDE_COLOR_HOME = $home_
try {
    Start-Process $Exe -ArgumentList '--screenshots', "`"$shots`"", '--both-themes' -Wait
}
finally { Remove-Item Env:CLAUDE_COLOR_HOME }

# Só páginas sem caminhos pessoais (Sistema e Solução de problemas mostram C:\Users\<nome>).
$map = @{
    '2-signals.png'        = 'sinais.png'
    '2-signals-escuro.png' = 'sinais-escuro.png'
    '3-devices.png'        = 'dispositivos.png'
    '4-profiles.png'       = 'perfis.png'
}
foreach ($k in $map.Keys) { Copy-Item (Join-Path $shots $k) (Join-Path "$root\docs" $map[$k]) -Force }
Remove-Item $home_ -Recurse -Force
Write-Host "Prints atualizados em docs\: $($map.Values -join ', '). Revise antes de commitar."

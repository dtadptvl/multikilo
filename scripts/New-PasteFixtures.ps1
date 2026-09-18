$ErrorActionPreference = "Stop"

$out = Join-Path $PSScriptRoot "..\paste-fixtures"
New-Item -ItemType Directory -Force -Path $out | Out-Null

function New-Line([int]$i) {
    return ("{0:D5} | Tiếng Việt: Trường Sa, Hoàng Sa, Nguyễn Ái Quốc | 日本語 | 한국어 | العربية | emoji: 😀 🧪 🚀 | symbols: []{}()<>" -f $i)
}

foreach ($count in 20, 200, 1000) {
    $path = Join-Path $out "$count-lines.txt"
    1..$count | ForEach-Object { New-Line $_ } | Set-Content -Encoding utf8 $path
}

$oneMbPath = Join-Path $out "1mb-mixed-unicode.txt"
$targetBytes = 1MB
$encoding = [System.Text.UTF8Encoding]::new($false)
$lines = [System.Collections.Generic.List[string]]::new()
$i = 1
$size = 0

while ($size -lt $targetBytes) {
    $line = New-Line $i
    $lines.Add($line)
    $size += $encoding.GetByteCount($line + [Environment]::NewLine)
    $i++
}

$text = [string]::Join([Environment]::NewLine, $lines)
[System.IO.File]::WriteAllText($oneMbPath, $text, $encoding)
Write-Host "Paste fixtures written to $out"

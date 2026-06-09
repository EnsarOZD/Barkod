# Gelistirme makinesinde calistirin, ardindan publish klasorunu depo PC'ye kopyalayin.

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "Yayinlaniyor (win-x64 self-contained)..." -ForegroundColor Cyan

dotnet publish "$ScriptDir\AkyildizKargoEtiket.csproj" `
    -c Release -r win-x64 --self-contained true `
    -o "$ScriptDir\publish"

if ($LASTEXITCODE -eq 0) {
    Write-Host "`nBasarili! Simdi publish klasorunu depo PC'ye kopyalayin." -ForegroundColor Green
    Write-Host "Klasor: $ScriptDir\publish" -ForegroundColor Gray
} else {
    Write-Host "`nHATA: Derleme basarisiz." -ForegroundColor Red
}

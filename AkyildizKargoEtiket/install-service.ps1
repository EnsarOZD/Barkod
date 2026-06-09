# AkyildizKargoEtiket Windows Service Kurulum Scripti
# Yonetici olarak calistirin: Right-click -> Run as Administrator
# NOT: Bu script publish klasorunu hazir bekler.
#      Gelistirme makinesinde once publish.ps1 calistirin.

$ServiceName = "AkyildizKargoEtiket"
$DisplayName = "Akyildiz Kargo Etiket Servisi"
$Description = "Sevkiyat sisteminden gelen kargo etiket islerini yaziciya gonderir."
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$ExePath     = Join-Path $ScriptDir "publish\AkyildizKargoEtiket.exe"

Write-Host "=== AkyildizKargoEtiket Kurulum ===" -ForegroundColor Cyan

# 1. Exe kontrolu
Write-Host "`n[1/3] Exe kontrol ediliyor..." -ForegroundColor Yellow
if (-not (Test-Path $ExePath)) {
    Write-Host "HATA: $ExePath bulunamadi." -ForegroundColor Red
    Write-Host "Lutfen gelistirme makinesinde publish.ps1 calistirip publish klasorunu buraya kopyalayin." -ForegroundColor Yellow
    exit 1
}
Write-Host "  Exe bulundu: $ExePath" -ForegroundColor Gray

# 2. Eski servisi kaldir
Write-Host "[2/3] Eski servis kontrol ediliyor..." -ForegroundColor Yellow
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "  Eski servis kaldirildi." -ForegroundColor Gray
}

# 3. Yeni servisi kur
Write-Host "[3/3] Servis kuruluyor..." -ForegroundColor Yellow
New-Service -Name $ServiceName -DisplayName $DisplayName -Description $Description `
    -BinaryPathName $ExePath -StartupType Automatic | Out-Null

Start-Service -Name $ServiceName

$status = (Get-Service -Name $ServiceName).Status
if ($status -eq "Running") {
    Write-Host "`nServis basariyla kuruldu ve calisiyor!" -ForegroundColor Green
    Write-Host "  Servis Adi : $ServiceName" -ForegroundColor Gray
    Write-Host "  Exe Yolu   : $ExePath" -ForegroundColor Gray
    Write-Host "`n  appsettings.json dosyasindaki AgentKey ve ServerUrl ayarlarini kontrol edin." -ForegroundColor Yellow
} else {
    Write-Host "`nServis kuruldu fakat calismiyor (Durum: $status)." -ForegroundColor Red
    Write-Host "  Event Viewer veya servis loglarini kontrol edin." -ForegroundColor Gray
}

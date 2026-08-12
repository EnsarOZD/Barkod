# AkyildizKargoEtiket — MEVCUT kurulumu guncelle (depo PC'sinde calistirin)
# Yonetici olarak: Right-click -> Run as Administrator
#
# Ilk kurulum icin bu degil, install-service.ps1 kullanilir.
#
# Bu script servisi durdurur, dosyalari kopyalar, servisi baslatir.
# appsettings.json'a DOKUNMAZ — gercek AgentKey orada durur.
#
# Kullanim (yeni dosyalar baska bir klasordeyse):
#   .\update-service.ps1 -SourceDir "C:\Users\depo\Desktop\publish"
# Kaynak verilmezse script'in yanindaki publish klasoru kullanilir.

param(
    [string] $SourceDir = ""
)

$ErrorActionPreference = "Stop"

$ServiceName = "AkyildizKargoEtiket"
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$TargetDir   = Join-Path $ScriptDir "publish"
if ([string]::IsNullOrWhiteSpace($SourceDir)) { $SourceDir = $TargetDir }

Write-Host "=== AkyildizKargoEtiket Guncelleme ===" -ForegroundColor Cyan
Write-Host "  Kaynak : $SourceDir"  -ForegroundColor Gray
Write-Host "  Hedef  : $TargetDir"  -ForegroundColor Gray

# ── 1. On kontroller ─────────────────────────────────────────────────────────
Write-Host "`n[1/6] On kontroller..." -ForegroundColor Yellow

if (-not (Test-Path (Join-Path $SourceDir "AkyildizKargoEtiket.exe"))) {
    Write-Host "HATA: Kaynakta AkyildizKargoEtiket.exe yok: $SourceDir" -ForegroundColor Red
    exit 1
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "HATA: '$ServiceName' servisi kurulu degil." -ForegroundColor Red
    Write-Host "  Ilk kurulum icin install-service.ps1 kullanin." -ForegroundColor Yellow
    exit 1
}

# Config kaynakta VARSA hedeftekini ezer — publish.ps1 bunu ciktidan siliyor,
# ama elle hazirlanmis bir klasor gelirse burada yakalayalim.
if (Test-Path (Join-Path $SourceDir "appsettings.json")) {
    Write-Host "HATA: Kaynak klasorde appsettings.json var." -ForegroundColor Red
    Write-Host "  Kopyalanirsa depo PC'sindeki gercek AgentKey ezilir ve etiketler" -ForegroundColor Yellow
    Write-Host "  sessizce basilmaz hale gelir. Dosyayi kaynaktan silip tekrar deneyin." -ForegroundColor Yellow
    exit 1
}

$cfgPath = Join-Path $TargetDir "appsettings.json"
if (-not (Test-Path $cfgPath)) {
    Write-Host "HATA: Hedefte appsettings.json yok: $cfgPath" -ForegroundColor Red
    Write-Host "  Guncelleme degil, ilk kurulum yapiyor olabilirsiniz." -ForegroundColor Yellow
    exit 1
}
Write-Host "  Servis bulundu (durum: $($svc.Status)), config yerinde." -ForegroundColor Gray

# ── 2. Config yedegi ─────────────────────────────────────────────────────────
Write-Host "[2/6] Config yedekleniyor..." -ForegroundColor Yellow
$stamp  = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path $ScriptDir "appsettings.backup-$stamp.json"
Copy-Item $cfgPath $backup -Force
Write-Host "  $backup" -ForegroundColor Gray

# ── 3. Servisi durdur ────────────────────────────────────────────────────────
Write-Host "[3/6] Servis durduruluyor..." -ForegroundColor Yellow
if ($svc.Status -ne "Stopped") {
    Stop-Service -Name $ServiceName -Force
    (Get-Service $ServiceName).WaitForStatus("Stopped", "00:00:30")
}
Write-Host "  Durdu." -ForegroundColor Gray

# ── 4. Dosyalari kopyala (appsettings.json haric) ────────────────────────────
Write-Host "[4/6] Dosyalar kopyalaniyor..." -ForegroundColor Yellow
if ($SourceDir -ne $TargetDir) {
    robocopy $SourceDir $TargetDir /E /NFL /NDL /NJH /NJS /NP /XF appsettings.json | Out-Null
    # robocopy 0-7 arasi cikis kodlari basari sayilir
    if ($LASTEXITCODE -ge 8) {
        Write-Host "HATA: Kopyalama basarisiz (robocopy $LASTEXITCODE)." -ForegroundColor Red
        Start-Service -Name $ServiceName
        exit 1
    }
    Write-Host "  Kopyalandi." -ForegroundColor Gray
} else {
    Write-Host "  Kaynak = hedef, kopyalama atlandi." -ForegroundColor Gray
}

# ── 5. Servisi baslat ────────────────────────────────────────────────────────
Write-Host "[5/6] Servis baslatiliyor..." -ForegroundColor Yellow
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3

# ── 6. Dogrula ───────────────────────────────────────────────────────────────
Write-Host "[6/6] Dogrulama..." -ForegroundColor Yellow
$status = (Get-Service -Name $ServiceName).Status
if ($status -eq "Running") {
    Write-Host "`nGuncelleme tamam — servis calisiyor." -ForegroundColor Green
    Write-Host "  Simdi Sevkiyat > Ayarlar > Yazicilar ekraninda agent'in" -ForegroundColor Gray
    Write-Host "  'cevrimici' gorundugunu teyit edin (15 sn icinde yesillenir)." -ForegroundColor Gray
} else {
    Write-Host "`nSERVIS BASLAMADI (durum: $status)." -ForegroundColor Red
    Write-Host "  En olasi sebep: appsettings.json bozuk/eksik." -ForegroundColor Yellow
    Write-Host "  Yedek: $backup" -ForegroundColor Yellow
    Write-Host "  Detay icin: Event Viewer > Windows Logs > Application" -ForegroundColor Gray
    exit 1
}

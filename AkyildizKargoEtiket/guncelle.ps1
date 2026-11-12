# AkyildizKargoEtiket - TEK KOMUTLA KURULUM / GUNCELLEME (depo PC'sinde, Yonetici PowerShell)
#
#   irm https://raw.githubusercontent.com/EnsarOZD/Barkod/main/AkyildizKargoEtiket/guncelle.ps1 | iex
#
# ya da bu dosyayi bir kez masaustune kaydedip:  powershell -ExecutionPolicy Bypass -File .\guncelle.ps1
#
# Ne yapar:
#   1. GitHub'dan main dalinin zip'ini indirir, icinden AkyildizKargoEtiket\publish klasorunu alir
#      (publish.ps1 ciktisi git'e commit'lenir - depo PC'sinde git/.NET/sifre gerekmez).
#   2. Servis KURULUYSA: klasoru servis kaydindan bulur, appsettings.json'i yedekler, servisi durdurur,
#      dosyalari kopyalar (appsettings.json HARIC - gercek AgentKey orada), servisi baslatir, dogrular.
#   3. Servis KURULU DEGILSE: ilk kurulum - klasoru olusturur, AgentKey'i sorar, appsettings.json'i
#      yazar, servisi kurar ve baslatir.
#
# Neden appsettings.json'a dokunulmuyor: register ucu her AgentKey'i kabul edip YENI agent acar;
# anahtar ezilirse yazici tanimlari eski agent'a bagli kalir ve etiketler SESSIZCE basilmaz (DEPLOY.md).
#
# Dal degistirmek icin (deneme):  $env:AJAN_DAL = "feat/pallet-label"  ve sonra komutu calistirin.
# 'exit' yerine 'throw' kullanilir: irm | iex ile calisirken exit PowerShell penceresini kapatir.

function Install-AkyildizAjan {
    $ErrorActionPreference = "Stop"
    $ServiceName = "AkyildizKargoEtiket"
    $DisplayName = "Akyildiz Kargo Etiket Servisi"
    $Description = "Sevkiyat sisteminden gelen etiket islerini yaziciya gonderir."
    $Repo        = "EnsarOZD/Barkod"
    $Dal         = if ($env:AJAN_DAL) { $env:AJAN_DAL } else { "main" }
    $VarsayilanKlasor = "C:\AkyildizKargoEtiket\publish"

    Write-Host "=== Akyildiz Etiket Ajani - Kurulum / Guncelleme ===" -ForegroundColor Cyan
    Write-Host "  Kaynak: github.com/$Repo ($Dal)" -ForegroundColor Gray

    # -- 0. Yonetici mi? --------------------------------------------------------------------
    $kimlik = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $kimlik.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Yonetici olarak calistirin: PowerShell'e sag tik -> 'Yonetici olarak calistir'."
    }

    # -- 1. Indir ve ac -----------------------------------------------------------------------
    Write-Host "`n[1/5] Indiriliyor..." -ForegroundColor Yellow
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $tmp = Join-Path $env:TEMP ("akyildiz-ajan-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    $zip = Join-Path $tmp "ajan.zip"
    $ProgressPreference = "SilentlyContinue"   # PS 5.1'de ilerleme cubugu indirmeyi cok yavaslatiyor
    Invoke-WebRequest -Uri "https://github.com/$Repo/archive/refs/heads/$Dal.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $kok = Get-ChildItem $tmp -Directory | Select-Object -First 1
    $Kaynak = Join-Path $kok.FullName "AkyildizKargoEtiket\publish"
    if (-not (Test-Path (Join-Path $Kaynak "AkyildizKargoEtiket.exe"))) {
        throw "Indirilen pakette AkyildizKargoEtiket.exe yok: $Kaynak"
    }
    if (Test-Path (Join-Path $Kaynak "appsettings.json")) {
        throw "Pakette appsettings.json var - kopyalanirsa AgentKey ezilir. Gelistirme makinesinde publish.ps1 ile yeniden yayinlayin."
    }
    $surum = (Get-Item (Join-Path $Kaynak "AkyildizKargoEtiket.exe")).LastWriteTime
    Write-Host "  Indirildi (derleme: $surum)." -ForegroundColor Gray

    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        # -- GUNCELLEME -------------------------------------------------------------------------
        $yol = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").PathName.Trim().Trim('"')
        $Hedef = Split-Path -Parent $yol
        Write-Host "`n[2/5] Kurulu servis bulundu: $Hedef (durum: $($svc.Status))" -ForegroundColor Yellow
        $cfg = Join-Path $Hedef "appsettings.json"
        if (-not (Test-Path $cfg)) { throw "Kurulu klasorde appsettings.json yok: $cfg - once onu duzeltin." }
        $yedek = Join-Path (Split-Path -Parent $Hedef) ("appsettings.backup-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
        Copy-Item $cfg $yedek -Force
        Write-Host "  Config yedegi: $yedek" -ForegroundColor Gray

        Write-Host "[3/5] Servis durduruluyor..." -ForegroundColor Yellow
        if ($svc.Status -ne "Stopped") {
            Stop-Service -Name $ServiceName -Force
            (Get-Service $ServiceName).WaitForStatus("Stopped", "00:00:30")
        }

        Write-Host "[4/5] Dosyalar kopyalaniyor (appsettings.json haric)..." -ForegroundColor Yellow
        robocopy $Kaynak $Hedef /E /NFL /NDL /NJH /NJS /NP /XF appsettings.json | Out-Null
        if ($LASTEXITCODE -ge 8) {
            Start-Service -Name $ServiceName
            throw "Kopyalama basarisiz (robocopy $LASTEXITCODE). Servis eski surumle yeniden baslatildi."
        }
    } else {
        # -- ILK KURULUM ------------------------------------------------------------------------
        $Hedef = $VarsayilanKlasor
        Write-Host "`n[2/5] Servis kurulu degil - ILK KURULUM: $Hedef" -ForegroundColor Yellow
        New-Item -ItemType Directory -Path $Hedef -Force | Out-Null
        robocopy $Kaynak $Hedef /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Kopyalama basarisiz (robocopy $LASTEXITCODE)." }

        Write-Host "[3/5] Ayarlar..." -ForegroundColor Yellow
        $anahtar = Read-Host "  AgentKey (bos birakirsaniz yeni bir anahtar uretilir)"
        if ([string]::IsNullOrWhiteSpace($anahtar)) { $anahtar = [guid]::NewGuid().ToString() }
        $ad = Read-Host "  Gorunen ad (bos: Ana Depo)"
        if ([string]::IsNullOrWhiteSpace($ad)) { $ad = "Ana Depo" }
        $ornek = Get-Content (Join-Path $Hedef "appsettings.example.json") -Raw | ConvertFrom-Json
        $ornek.Agent.AgentKey = $anahtar.Trim()
        $ornek.Agent.DisplayName = $ad.Trim()
        $ornek | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Hedef "appsettings.json") -Encoding UTF8
        Write-Host "  appsettings.json yazildi. AgentKey: $anahtar" -ForegroundColor Gray

        Write-Host "[4/5] Servis kuruluyor..." -ForegroundColor Yellow
        New-Service -Name $ServiceName -DisplayName $DisplayName -Description $Description `
            -BinaryPathName (Join-Path $Hedef "AkyildizKargoEtiket.exe") -StartupType Automatic | Out-Null
    }

    # -- 5. Baslat ve dogrula --------------------------------------------------------------------
    Write-Host "[5/5] Servis baslatiliyor..." -ForegroundColor Yellow
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 3
    $durum = (Get-Service -Name $ServiceName).Status
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if ($durum -ne "Running") {
        throw "SERVIS BASLAMADI (durum: $durum). Event Viewer > Windows Logs > Application'a bakin; en olasi sebep appsettings.json."
    }
    Write-Host "`nTamam - servis calisiyor ($Hedef)." -ForegroundColor Green
    Write-Host "  Sevkiyat > Ayarlar > Yazicilar'da ajan 15 sn icinde 'cevrimici' gorunmeli; bir test baskisi alin." -ForegroundColor Gray
}

Install-AkyildizAjan

# Gelistirme makinesinde calistirin, ardindan publish klasorunu depo PC'ye kopyalayin.
#
# ONEMLI: Yayin ciktisindan appsettings.json KASITLI olarak silinir; yerine
# appsettings.example.json birakilir. Sebep: depo PC'sindeki appsettings.json
# gercek AgentKey'i tasiyor. Klasor oldugu gibi kopyalanirsa o dosya sablonla
# ezilir ve agent kendini YENI bir agent olarak kaydeder — yazici tanimlari eski
# agent'a bagli kaldigi icin hicbir is gelmez, hicbir yerde de hata gorunmez.
# Ciktida appsettings.json bulunmayinca kopyalama bu dosyaya hic dokunamaz.

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PublishDir = Join-Path $ScriptDir "publish"

# Eski cikti temizlenir: dotnet publish klasoru temizlemez, kaldirilan dosyalar
# aksi halde orada kalir ve depo PC'sine tasinir.
if (Test-Path $PublishDir) {
    Write-Host "Eski yayin temizleniyor..." -ForegroundColor Gray
    Remove-Item "$PublishDir\*" -Recurse -Force
}

Write-Host "Yayinlaniyor (win-x64 self-contained)..." -ForegroundColor Cyan

dotnet publish "$ScriptDir\AkyildizKargoEtiket.csproj" `
    -c Release -r win-x64 --self-contained true `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "`nHATA: Derleme basarisiz." -ForegroundColor Red
    exit 1
}

# Yapilandirmayi ciktidan cikar, ornek olarak birak.
$cfg     = Join-Path $PublishDir "appsettings.json"
$example = Join-Path $PublishDir "appsettings.example.json"
if (Test-Path $cfg) {
    Move-Item -Path $cfg -Destination $example -Force
    Write-Host "  appsettings.json -> appsettings.example.json (config ezilmesin diye)" -ForegroundColor Gray
}
Remove-Item (Join-Path $PublishDir "appsettings.Development.json") -Force -ErrorAction SilentlyContinue

Write-Host "`nBasarili." -ForegroundColor Green
Write-Host "  Klasor: $PublishDir" -ForegroundColor Gray
Write-Host "`nSonraki adim:" -ForegroundColor Cyan
Write-Host "  GUNCELLEME  -> publish klasorunu depo PC'ye kopyalayip update-service.ps1 calistirin" -ForegroundColor Gray
Write-Host "  ILK KURULUM -> appsettings.example.json'u appsettings.json yapip AgentKey/ServerUrl" -ForegroundColor Gray
Write-Host "                 doldurun, sonra install-service.ps1 calistirin" -ForegroundColor Gray
Write-Host "`nAyrintili adimlar: DEPLOY.md" -ForegroundColor Gray

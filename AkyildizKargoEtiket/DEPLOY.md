# Etiket Agent — Kurulum ve Güncelleme

Agent, **depo PC'sinde** çalışan bir Windows servisidir. Sevkiyat API'sini 3 saniyede bir
yoklar, bekleyen baskı işini alır, ZPL üretip Win32 `winspool` ile yazıcıya RAW gönderir.

- Yayın **self-contained win-x64** — depo PC'sinde .NET kurulu olmasına gerek yok.
- Sunucuya bağlantı **giden HTTPS**; agent'a dışarıdan port açılmaz.
- Kimlik: `appsettings.json` içindeki **`AgentKey`**.

---

## ⚠️ Tek büyük tuzak: `appsettings.json`

`POST /api/print/agents/register` **anonim** ve **herhangi bir `AgentKey`'i kabul edip
yeni bir agent satırı açıyor**. Yanlış anahtarla çalışan agent hata vermez — kendini
*yeni* bir agent olarak kaydeder. Yazıcı tanımları (`PrinterConfig.AgentId`) eski agent'a
bağlı kaldığı için `jobs/pending` sonsuza kadar boş döner:

> **Etiketler sessizce basılmaz. Ne serviste, ne API'de, ne arayüzde hata görünür.**
> Tek belirti: Ayarlar → Yazıcılar'da agent "çevrimdışı" kalır, kuyruktaki işler `Pending` birikir.

Bu yüzden:

1. `publish.ps1` yayın çıktısından `appsettings.json`'ı **siler**, yerine
   `appsettings.example.json` bırakır → klasörü kopyalamak config'i ezemez.
2. `update-service.ps1` kaynakta `appsettings.json` görürse **durur**.
3. Agent açılışta anahtarı doğrular; şablon değer (`BURAYA...`) duruyorsa
   **servis hiç başlamaz** (sessiz hata yerine gürültülü hata).

---

## Güncelleme (mevcut kurulum var)

### 1 — Geliştirme makinesi

```powershell
cd "D:\Barkod Yazma\Barkod\AkyildizKargoEtiket"
.\publish.ps1
```

`publish\` klasörünü depo PC'sine taşıyın (USB / ağ paylaşımı / RDP kopyala-yapıştır).
İçinde `appsettings.json` **olmamalı** — `publish.ps1` bunu zaten garantiliyor.

### 2 — Depo PC'si (Yönetici PowerShell)

```powershell
# Kurulu klasörün yanında, yeni dosyalar örn. masaüstünde:
.\update-service.ps1 -SourceDir "C:\Users\<kullanici>\Desktop\publish"
```

Script sırayla: ön kontroller → config yedeği → servis durdur → dosyaları kopyala
(`appsettings.json` hariç) → servis başlat → durum doğrula.

### 3 — Doğrulama

- **Sevkiyat → Ayarlar → Yazıcılar**: agent 15 saniye içinde "çevrimiçi" olmalı.
- Bir test baskısı gönderin, iş `Done`'a geçmeli.

### Geri alma

Sorun çıkarsa: servisi durdurun, bir önceki `publish` klasörünü geri kopyalayın,
`appsettings.backup-*.json` dosyasını `appsettings.json` olarak geri yazın, servisi başlatın.
Yedek dosya `update-service.ps1`'in bulunduğu klasöre zaman damgalı olarak bırakılır.

---

## İlk kurulum (yeni PC)

1. `publish.ps1` çıktısını PC'ye kopyalayın.
2. `publish\appsettings.example.json` → `publish\appsettings.json` olarak kopyalayın ve doldurun:

```json
{
  "Agent": {
    "ServerUrl": "https://api.sevkiyat.akyildizlojistik.com/api",
    "AgentKey": "<yeni-uuid>",
    "DisplayName": "Ana Depo",
    "PollIntervalSeconds": 3
  }
}
```

`AgentKey` serbest bir UUID'dir; agent ilk `register` çağrısında kendini bu anahtarla
oluşturur. **Not:** `ServerUrl` **API** ana bilgisayarıdır (`api.` ön ekli), SPA adresi değil.

3. Yönetici PowerShell'de `.\install-service.ps1` — servisi kurar ve başlatır.
4. Yazıcıyı Windows'a kurun, sonra **Ayarlar → Yazıcılar**'dan bir `PrinterConfig` açın:
   etiket tipini seçin, agent'ı seçin, Windows yazıcı adını girin, **Varsayılan** işaretleyin.
   Bu kayıt olmadan o tipte iş oluşturulamaz.

---

## Etiket tipleri ve yazıcı eşleşmesi

| Tip | Değer | Boyut | Notu |
|-----|-------|-------|------|
| Kargo Etiketi | 0 | 76 × 51 mm | `^PW609 ^LL406` |
| Koli Etiketi | 1 | 100 × 80 mm | `^PW800 ^LL640` |
| Palet Etiketi | 2 | 100 × 80 mm | Koli ile **aynı rulo** — aynı yazıcı kullanılabilir |

Her tip için ayrı `PrinterConfig` gerekir (aynı Windows yazıcısını gösterseler bile),
çünkü iş oluşturma o tipin `IsDefault` kaydını arar.

### Sürüm sırası — önemli

Yeni bir etiket tipi eklendiğinde **önce agent, sonra API** güncellenir.
Agent eski API ile uyumlu çalışmaya devam eder; tersi doğru değildir — tanımadığı
`LabelType` gelirse iş `Failed` olur (servis çökmez, `PrintAgentWorker` her işi ayrı
`try/catch` içinde çalıştırır).

---

## Sorun giderme

| Belirti | Bakılacak yer |
|---------|---------------|
| Servis başlamıyor | Event Viewer → Windows Logs → Application. En sık sebep: `appsettings.json` eksik/bozuk ya da `AgentKey` şablon değerde |
| Agent "çevrimdışı" | Depo PC'sinden `https://api.sevkiyat.akyildizlojistik.com/api` erişimi; `ServerUrl` doğru mu (`api.` ön eki) |
| İşler `Pending` birikiyor | `PrinterConfig.AgentId` doğru agent'ı gösteriyor mu — **anahtar ezilme senaryosu** (yukarı bakın) |
| İş `Failed`, "Bilinmeyen etiket tipi" | Agent eski sürüm; güncelleyin |
| İş `Failed`, "Yazıcı açılamadı" | Windows yazıcı adı `PrinterConfig.WindowsPrinterName` ile birebir aynı mı |
| Türkçe karakter bozuk | `^CI28` + UTF-8 kullanılmalı. `Encoding.GetEncoding(1254)` .NET 10'da patlıyor — geri getirmeyin |

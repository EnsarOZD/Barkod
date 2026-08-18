using Microsoft.Extensions.Options;
using System.Drawing.Printing;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
 
namespace AkyildizKargoEtiket;
 
// ── API DTOs ─────────────────────────────────────────────────────────────────
 
public record RegisterRequest(string AgentKey, string MachineName, string DisplayName, List<string> InstalledPrinters);
/// <summary><c>Language</c>: 0 = ZPL, 1 = EPL. Eski API sürümü bu alanı göndermezse 0 kalır.</summary>
public record PendingJob(int Id, int LabelType, string PayloadJson, string PrinterName, int Language = 0);
public record UpdateStatusRequest(int Status, string? ErrorMessage);
/// <summary><c>PieceNo</c>/<c>PieceTotal</c>: çok kolili gönderide "2/3" sırası. Eski API bunları göndermezse 1/1 kalır.</summary>
public record CargoLabelPayload(string Barcode, string ReceiverName, string Address, string Phone, string? IrsaliyeNo, int ShipmentId, string DeliveryDate, int PieceNo = 1, int PieceTotal = 1);
public record BoxLabelPayload(string ProjectName, string Location, string ProjectCode, int BoxCount);
public record PalletLabelPayload(string PalletCode, string ReceivedDate, string? SupplierName, int LineCount, decimal TotalQty);
public record ProductLabelPayload(string Barcode, string StockCode, string StockName, string? Unit);
 
// ── Print Service ─────────────────────────────────────────────────────────────
 
public class PrintAgentService
{
    private readonly HttpClient _http;
    private readonly AgentOptions _opts;
    private readonly ILogger<PrintAgentService> _log;
 
    public PrintAgentService(HttpClient http, IOptions<AgentOptions> opts, ILogger<PrintAgentService> log)
    {
        _http = http;
        _opts = opts.Value;
        _log  = log;

        ValidateOptions(_opts);

        _http.BaseAddress = new Uri(_opts.ServerUrl.TrimEnd('/') + "/");
        _http.Timeout     = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Açılışta yapılandırmayı doğrular; hata varsa servis hiç başlamaz.
    ///
    /// Neden gerekli: <c>POST print/agents/register</c> anonim ve HERHANGİ bir AgentKey'i
    /// kabul edip yeni bir agent satırı açıyor. Güncelleme sırasında depo PC'sindeki
    /// appsettings.json yayın klasöründeki şablonla EZİLİRSE, agent placeholder anahtarla
    /// kendini yeni bir agent olarak kaydeder; PrinterConfig kayıtları eski agent'a bağlı
    /// kaldığı için <c>jobs/pending</c> sonsuza kadar boş döner. Hiçbir yerde hata görünmez,
    /// etiketler sessizce basılmaz. Bu kontrol o senaryoyu gürültülü hâle getirir.
    /// </summary>
    private static void ValidateOptions(AgentOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.ServerUrl))
            throw new InvalidOperationException(
                "Agent:ServerUrl boş. appsettings.json eksik ya da doldurulmamış olabilir.");

        if (!Uri.TryCreate(o.ServerUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Agent:ServerUrl geçerli bir adres değil: '{o.ServerUrl}'");

        if (string.IsNullOrWhiteSpace(o.AgentKey) || o.AgentKey.Contains("BURAYA", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Agent:AgentKey doldurulmamış (şablon değeri duruyor). Depo PC'sindeki appsettings.json " +
                "güncelleme sırasında ezilmiş olabilir — eski AgentKey'i geri yazın. " +
                "Yanlış anahtarla çalışırsa agent kendini YENİ bir agent olarak kaydeder ve " +
                "yazıcı tanımları eski agent'ta kaldığı için hiçbir iş gelmez.");

        if (o.PollIntervalSeconds < 1)
            throw new InvalidOperationException("Agent:PollIntervalSeconds en az 1 olmalı.");
    }
 
    public List<string> GetInstalledPrinters()
    {
        var printers = new List<string>();
        foreach (string printer in PrinterSettings.InstalledPrinters)
            printers.Add(printer);
        return printers;
    }
 
    public async Task RegisterAsync(CancellationToken ct)
    {
        var req = new RegisterRequest(
            _opts.AgentKey,
            Environment.MachineName,
            _opts.DisplayName,
            GetInstalledPrinters());
 
        var resp = await _http.PostAsJsonAsync("print/agents/register", req, ct);
        if (!resp.IsSuccessStatusCode)
            _log.LogWarning("Kayıt başarısız: {Status}", resp.StatusCode);
        else
            _log.LogInformation("Agent kaydedildi.");
    }
 
    public async Task<List<PendingJob>> GetPendingJobsAsync(CancellationToken ct)
    {
        var resp = await _http.GetAsync($"print/jobs/pending?agentKey={Uri.EscapeDataString(_opts.AgentKey)}", ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<PendingJob>>(ct) ?? [];
    }
 
    public async Task UpdateJobStatusAsync(int jobId, int status, string? error, CancellationToken ct)
    {
        var req = new UpdateStatusRequest(status, error);
        await _http.PatchAsJsonAsync($"print/jobs/{jobId}/status?agentKey={Uri.EscapeDataString(_opts.AgentKey)}", req, ct);
    }
 
    public void PrintJob(PendingJob job)
    {
        // Şablon (etiket tipi, yazıcı dili) çiftine göre seçilir. Dil yazıcının
        // fiziksel özelliği: ZPL bilmeyen bir cihaza (ör. Zebra LP2844) ZPL gönderilirse
        // cihaz komutları sessizce yutar — iş "başarılı" görünür, kâğıt çıkmaz.
        string data = (job.LabelType, job.Language) switch
        {
            (0, 0) => BuildCargoZpl(job),
            (1, 0) => BuildBoxZpl(job),
            (2, 0) => BuildPalletZpl(job),

            (3, 0) => BuildProductZpl(job),

            (0, 1) => BuildCargoEpl(job),
            (1, 1) => BuildBoxEpl(job),
            (2, 1) => BuildPalletEpl(job),
            (3, 1) => BuildProductEpl(job),

            _ => throw new InvalidOperationException(
                $"Desteklenmeyen kombinasyon: etiket tipi {job.LabelType}, dil " +
                (job.Language == 1 ? "EPL" : "ZPL"))
        };

        SendRawToPrinter(job.PrinterName, data, job.Language == 1);
        _log.LogInformation("Baskı tamamlandı: Job#{Id} → {Printer} ({Lang})",
            job.Id, job.PrinterName, job.Language == 1 ? "EPL" : "ZPL");
    }
 
    // ── ZPL Builders ─────────────────────────────────────────────────────────
 
    private static string BuildCargoZpl(PendingJob job)
    {
        var p = JsonSerializer.Deserialize<CargoLabelPayload>(job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Kargo payload parse edilemedi.");
 
        var name    = Truncate(p.ReceiverName.ToUpper(TurkishCulture), 26);
        var phone   = Truncate(p.Phone, 24);
        var address = WrapText(p.Address, 48, 2);

        // Ekrandaki A4 etiketiyle aynı düzen: başlık → ALICI → barkod → künye → gönderici.
        // Ölçü 100 x 80 mm (800 x 640 dot) — EPL sürümüyle aynı rulo.
        var sb = new StringBuilder();
        sb.Append("^XA\n^CI28\n^PW800\n^LL640\n");

        int y = 40;

        // ── Başlık ───────────────────────────────────────────────────────
        sb.Append($"^FO40,{y}^A0N,44,44^FDAKYILDIZ LOJİSTİK^FS\n");
        sb.Append($"^FO570,{y + 12}^A0N,24,24^FDYURTİÇİ KARGO^FS\n");
        y += 58;
        sb.Append($"^FO40,{y}^GB720,3,3^FS\n");
        y += 16;

        // ── Alıcı ────────────────────────────────────────────────────────
        sb.Append($"^FO40,{y}^A0N,18,18^FDALICI^FS\n");
        y += 24;
        sb.Append($"^FO40,{y}^A0N,40,40^FD{name}^FS\n");
        y += 50;
        foreach (var line in address)
        {
            sb.Append($"^FO40,{y}^A0N,24,24^FD{line}^FS\n");
            y += 28;
        }
        if (phone.Length > 0)
        {
            sb.Append($"^FO40,{y}^A0N,24,24^FDTel: {phone}^FS\n");
            y += 28;
        }

        sb.Append($"^FO40,{y}^GB720,3,3^FS\n");
        y += 14;

        // ── Barkod ───────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(p.Barcode))
        {
            var code = p.Barcode.Trim();
            int module = 4;
            while (module > 2 && Code128Width(code, module) > 720) module--;
            int barX = Math.Max(40, (800 - Code128Width(code, module)) / 2);

            sb.Append($"^FO{barX},{y}^BY{module}^BCN,100,N,N,N^FD{code}^FS\n");
            y += 108;
            sb.Append($"^FO{CenterX(code, 800, 22)},{y}^A0N,26,26^FD{code}^FS\n");
            y += 34;
        }

        sb.Append($"^FO40,{y}^GB720,3,3^FS\n");
        y += 14;

        // ── Künye ────────────────────────────────────────────────────────
        sb.Append($"^FO40,{y}^A0N,24,24^FDSevkiyat: #{p.ShipmentId}   Tarih: {p.DeliveryDate}{PieceSuffix(p)}^FS\n");
        y += 28;

        if (!string.IsNullOrWhiteSpace(p.IrsaliyeNo))
        {
            sb.Append($"^FO40,{y}^A0N,24,24^FDİrsaliye: {p.IrsaliyeNo}^FS\n");
            y += 28;
        }

        sb.Append($"^FO40,{y}^GB720,3,3^FS\n");
        y += 14;

        // ── Gönderici ────────────────────────────────────────────────────
        sb.Append($"^FO40,{y}^A0N,24,24^FDGÖNDERİCİ: Akyıldız Lojistik A.Ş.^FS\n");
        sb.Append("^XZ");

        return sb.ToString();
    }
 
    private static string BuildBoxZpl(PendingJob job)
    {
        var p = JsonSerializer.Deserialize<BoxLabelPayload>(job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Koli payload parse edilemedi.");
 
        var name = Truncate(p.ProjectName.ToUpperInvariant(), 18);
        var loc  = Truncate(p.Location.ToUpperInvariant(),    18);
        var code = $"{p.ProjectCode} ({p.BoxCount} KOLİ)";
 
        return $@"^XA
^CI28
^PW800
^LL640
^CF0,80
^FO{CenterX(name, 800, 60)},120^FD{name}^FS
^CF0,60
^FO{CenterX(loc, 800, 44)},260^FD{loc}^FS
^CF0,50
^FO{CenterX(code, 800, 37)},400^FD{code}^FS
^XZ";
    }

    /// <summary>
    /// Palet etiketi — 100 x 80 mm (800 x 640 dot @ 203 dpi), koli etiketiyle aynı rulo.
    /// Yerleşim: palet no (dev punto) · teslim tarihi · Code128 · içerik özeti · tedarikçi.
    /// Tarama hedefi barkodun içeriği = PalletCode'un kendisi (parse yok).
    /// </summary>
    private static string BuildPalletZpl(PendingJob job)
    {
        var p = JsonSerializer.Deserialize<PalletLabelPayload>(job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Palet payload parse edilemedi.");

        if (string.IsNullOrWhiteSpace(p.PalletCode))
            throw new InvalidDataException("Palet kodu boş — etiket basılamaz.");

        var code    = p.PalletCode.Trim().ToUpperInvariant();
        var summary = $"{p.LineCount} KALEM · {FormatQty(p.TotalQty)} ADET";

        // Tedarikçi adı Türkçe büyük harf: ToUpperInvariant "i" → "I" verir, doğrusu "İ".
        // ^CI28 + UTF-8 zaten Türkçe karakterleri basıyor (bkz. SendZplToPrinter notu).
        var supplierRaw = (p.SupplierName ?? string.Empty).Trim();
        var supplier    = supplierRaw.Length == 0
            ? string.Empty
            : Truncate(supplierRaw.ToUpper(TurkishCulture), 32);

        // Palet kodu punto: uzun kod 110'da taşıyor (10 karakter x 81 dot = 810 > 800).
        // Sığan en büyük puntoyu seç — kod hiçbir zaman kırpılmaz, küçülür.
        var (codeFont, codeCharW) = FitFont(code, 720, 110, 92, 76, 62);

        // Barkod modül genişliği: etikete sığana kadar 4 → 2 arası daralt.
        int module = 4;
        while (module > 2 && Code128Width(code, module) > 740) module--;
        int barcodeX = Math.Max(30, (800 - Code128Width(code, module)) / 2);

        return $@"^XA
^CI28
^PW800
^LL640
^CF0,{codeFont}
^FO{CenterX(code, 800, codeCharW)},{30 + (110 - codeFont) / 2}^FD{code}^FS
^FO40,158^GB720,3,3^FS
^CF0,46
^FO{CenterX(p.ReceivedDate, 800, 34)},178^FD{p.ReceivedDate}^FS
^FO{barcodeX},248^BY{module}^BCN,160,N,N,N^FD{code}^FS
^FO40,432^GB720,3,3^FS
^CF0,34
^FO{CenterX(summary, 800, 25)},452^FD{summary}^FS{SupplierBlock(supplier)}
^XZ";
    }

    /// <summary>Tedarikçi satırı — ad boşsa hiç alan üretme (boş ^FD^FS bırakma).</summary>
    private static string SupplierBlock(string supplier) =>
        supplier.Length == 0
            ? string.Empty
            : $"\n^CF0,30\n^FO{CenterX(supplier, 800, 22)},502^FD{supplier}^FS";
 
    /// <summary>
    /// Ürün etiketi (ZPL) — 100 x 80 mm. Ambalajında üretici barkodu olmayan ürünlere
    /// yapıştırılır: ürün adı · stok kodu · Code128(barkod).
    /// </summary>
    private static string BuildProductZpl(PendingJob job)
    {
        var p = ParseProduct(job);
        var (nameFont, nameCharW) = FitFont(p.name, 720, 60, 50, 42, 34);

        int module = 4;
        while (module > 2 && Code128Width(p.barcode, module) > 740) module--;
        int barcodeX = Math.Max(30, (800 - Code128Width(p.barcode, module)) / 2);

        return $@"^XA
^CI28
^PW800
^LL640
^CF0,{nameFont}
^FO{CenterX(p.name, 800, nameCharW)},60^FD{p.name}^FS
^CF0,40
^FO{CenterX(p.code, 800, 29)},{60 + nameFont + 30}^FD{p.code}^FS
^FO40,{60 + nameFont + 90}^GB720,3,3^FS
^FO{barcodeX},{60 + nameFont + 120}^BY{module}^BCN,170,Y,N,N^FD{p.barcode}^FS
^XZ";
    }

    // ── EPL Builders ─────────────────────────────────────────────────────────

    /// <summary>
    /// Ürün etiketi (EPL2) — 100 x 80 mm. ZPL sürümüyle aynı içerik.
    /// Barkod tipi "3" = Code 128 Auto, insan-okur satır açık (son parametre B).
    /// </summary>
    private static string BuildProductEpl(PendingJob job)
    {
        var p = ParseProduct(job);

        int narrow = 3;
        while (narrow > 2 && Code128Width(p.barcode, narrow) > EplContentWidth) narrow--;
        int barX = Math.Max(EplLeftMargin, (EplLabelWidth - Code128Width(p.barcode, narrow)) / 2);

        var lines = new List<string>
        {
            "N",
            "q800",
            EplCharsetLine,
            EplCenteredLine(p.name, EplTopMargin, 72),
            EplCenteredLine(p.code, EplTopMargin + 90, 48),
            $"LO{EplLeftMargin},{EplTopMargin + 150},{EplContentWidth},3",
            $"B{barX},{EplTopMargin + 170},0,3,{narrow},{narrow * 2},150,B,\"{EplData(p.barcode)}\"",
            "P1",
        };

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// Ürün etiketi payload'ını okur ve iki üretici için ortak alanları hazırlar.
    /// Ürün adı iki satıra bölünmüyor — 26 karakterde kesiliyor; etikette barkodun
    /// okunabilirliği ada göre öncelikli.
    /// </summary>
    private static (string barcode, string code, string name) ParseProduct(PendingJob job)
    {
        var p = JsonSerializer.Deserialize<ProductLabelPayload>(job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Ürün payload parse edilemedi.");

        var barcode = Ascii((p.Barcode ?? string.Empty).Trim());
        if (barcode.Length == 0)
            throw new InvalidDataException("Barkod boş — etiket basılamaz.");

        var code = Ascii((p.StockCode ?? string.Empty).Trim().ToUpperInvariant());
        var name = EplTruncate(Ascii((p.StockName ?? string.Empty).Trim().ToUpper(TurkishCulture)), 26);

        return (barcode, code, name);
    }

    /// <summary>
    /// Palet etiketi — EPL2. ZPL bilmeyen cihazlar için: Zebra LP2844 yalnızca EPL konuşur,
    /// ZPL komutlarını sessizce yutar (hata da vermez), bu yüzden ZPL gönderilen işler
    /// "Tamamlandı" görünüp hiç basılmaz.
    ///
    /// Rulo 100 x 80 mm (800 x 640 dot @ 203 dpi) varsayılıyor. Genişlik <c>q</c> ile veriliyor;
    /// etiket boyu bilerek verilmiyor (<c>Q</c> yok) — yanlış gap değeri besleme hatası yapar,
    /// yazıcının kendi kalibrasyonu daha güvenilir.
    ///
    /// Barkod tipi "3" = Code 128 Auto. Bu değer LP2844 firmware'inde kâğıt üzerinde
    /// doğrulandı (2026-08-17): sec=3 ve sec=1 bastı, sec=0 (Code 39) basmadı.
    ///
    /// Türkçe: EPL2'nin varsayılan karakter seti Türkçe harfleri taşımıyor; doğru kod sayfası
    /// ZPL tarafında zaten soruna yol açmış olan Encoding.GetEncoding(1254)'e bağımlı olurdu.
    /// Bu yüzden metin ASCII'ye indirgeniyor (ALTINDAĞ → ALTINDAG). Depo etiketinde
    /// okunabilirlik kaybı yok, kırılma riski sıfır.
    /// </summary>
    private static string BuildPalletEpl(PendingJob job)
    {
        var p = JsonSerializer.Deserialize<PalletLabelPayload>(job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Palet payload parse edilemedi.");

        if (string.IsNullOrWhiteSpace(p.PalletCode))
            throw new InvalidDataException("Palet kodu boş — etiket basılamaz.");

        // Barkod verisi ASCII kalmalı — Code128'e Türkçe harf girmesin.
        var code = Ascii(p.PalletCode.Trim().ToUpperInvariant());

        // Sığan en büyük çarpanı seç — kod hiçbir zaman kırpılmaz, küçülür
        // (ZPL tarafındaki FitFont ile aynı mantık).
        int mult = 3;
        while (mult > 1 && code.Length * EplAdvance(32, mult) > EplContentWidth) mult--;

        // Code128 dar bar genişliği: sığana kadar 3 → 2.
        int narrow = 3;
        while (narrow > 2 && Code128Width(code, narrow) > EplContentWidth) narrow--;
        int barX = Math.Max(EplLeftMargin, (EplLabelWidth - Code128Width(code, narrow)) / 2);

        var supplier = EplTruncate((p.SupplierName ?? string.Empty).Trim().ToUpper(TurkishCulture), 22);
        var summary  = $"{p.LineCount} KALEM - {FormatQty(p.TotalQty)} ADET";

        // Y koordinatları TOP_MARGIN'den başlar: rulo Yurtiçi Kargo baskılı ve üst
        // banttaki logo kalıcı. İçerik y=0'dan başlarsa palet kodu logonun üstüne biner.
        var lines = new List<string>
        {
            "N",
            "q800",
            EplCharsetLine,
            $"A{EplLeftMargin},{EplTopMargin},0,5,{mult},{mult},N,\"{EplData(code)}\"",
            $"LO{EplLeftMargin},{EplTopMargin + 110},{EplContentWidth},3",
            $"A{EplLeftMargin},{EplTopMargin + 125},0,4,2,2,N,\"TARİH: {EplData(p.ReceivedDate)}\"",
            $"A{EplLeftMargin},{EplTopMargin + 180},0,4,2,2,N,\"{EplData(summary)}\"",
        };

        // Tedarikçi adı boşsa satırı hiç üretme.
        if (supplier.Length > 0)
            lines.Add($"A{EplLeftMargin},{EplTopMargin + 235},0,4,2,2,N,\"{EplData(supplier)}\"");

        // Tarama hedefi barkodun içeriği = PalletCode'un kendisi (parse yok).
        lines.Add($"B{barX},{EplTopMargin + 295},0,3,{narrow},{narrow * 2},120,B,\"{EplData(code)}\"");
        lines.Add("P1");

        // EPL satır sonu CRLF olmalı.
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// Kargo etiketi — EPL2 karşılığı. 76 x 51 mm (609 x 406 dot @ 203 dpi).
    /// ZPL sürümüyle (BuildCargoZpl) aynı bilgi düzeni: barkod → alıcı → adres/telefon →
    /// sevkiyat künyesi → gönderici satırı. Ölçüler EPL font matrislerine göre yeniden
    /// hesaplandı, ZPL koordinatları birebir taşınmadı (font metrikleri farklı).
    /// </summary>
    private static string BuildCargoEpl(PendingJob job)
    {
        var p = JsonSerializer.Deserialize<CargoLabelPayload>(job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Kargo payload parse edilemedi.");

        var barcode = Ascii((p.Barcode ?? string.Empty).Trim());   // Code128 verisi ASCII kalmalı
        var name    = EplTruncate(p.ReceiverName.ToUpper(TurkishCulture), 26);
        var phone   = EplTruncate(p.Phone, 24);

        // Adres iki satıra bölünüyor: tek satırda 25 karakterde kesiliyordu ve sokak
        // bilgisi kayboluyordu. Font 3 / 1x ile satır başına ~50 karakter sığıyor.
        var addressLines = WrapText(p.Address, 48, 2);

        // Ekrandaki A4 etiketiyle aynı düzen: başlık → ALICI → barkod → künye → gönderici.
        // Ölçü 100 x 80 mm (800 x 640 dot). İçerik EplTopMargin'den başlar; rulo Yurtiçi
        // baskılı ve üst bantta basılı logo var.
        var lines = new List<string> { "N", "q800", EplCharsetLine };
        int y = EplTopMargin;

        // ── Başlık: marka + kargo firması ────────────────────────────────
        lines.Add($"A{EplLeftMargin},{y},0,4,2,2,N,\"AKYILDIZ LOJİSTİK\"");
        lines.Add($"A560,{y + 12},0,3,1,1,N,\"YURTİÇİ KARGO\"");
        y += 56;
        lines.Add($"LO{EplLeftMargin},{y},{EplContentWidth},3");
        y += 14;

        // ── Alıcı ────────────────────────────────────────────────────────
        lines.Add($"A{EplLeftMargin},{y},0,2,1,1,N,\"ALICI\"");
        y += 22;
        lines.Add($"A{EplLeftMargin},{y},0,4,2,2,N,\"{EplData(name)}\"");
        y += 52;
        foreach (var line in addressLines)
        {
            lines.Add($"A{EplLeftMargin},{y},0,3,1,1,N,\"{EplData(line)}\"");
            y += 26;
        }
        if (phone.Length > 0)
        {
            lines.Add($"A{EplLeftMargin},{y},0,3,1,1,N,\"Tel: {EplData(phone)}\"");
            y += 26;
        }

        lines.Add($"LO{EplLeftMargin},{y},{EplContentWidth},3");
        y += 12;

        // ── Barkod ───────────────────────────────────────────────────────
        // Boşsa B komutu hata verir; barkodsuz etiket basmak hiç basmamaktan iyidir.
        if (barcode.Length > 0)
        {
            int narrow = 3;
            while (narrow > 2 && Code128Width(barcode, narrow) > EplContentWidth) narrow--;
            int barX = Math.Max(EplLeftMargin, (EplLabelWidth - Code128Width(barcode, narrow)) / 2);
            lines.Add($"B{barX},{y},0,3,{narrow},{narrow * 2},100,N,\"{EplData(barcode)}\"");
            y += 106;

            int textX = Math.Max(EplLeftMargin, (EplLabelWidth - barcode.Length * EplAdvance(12, 1)) / 2);
            lines.Add($"A{textX},{y},0,3,1,1,N,\"{EplData(barcode)}\"");
            y += 30;
        }

        lines.Add($"LO{EplLeftMargin},{y},{EplContentWidth},3");
        y += 12;

        // ── Künye ────────────────────────────────────────────────────────
        lines.Add($"A{EplLeftMargin},{y},0,3,1,1,N,\"Sevkiyat: #{p.ShipmentId}   Tarih: {EplData(p.DeliveryDate)}{EplData(PieceSuffix(p))}\"");
        y += 26;

        if (!string.IsNullOrWhiteSpace(p.IrsaliyeNo))
        {
            lines.Add($"A{EplLeftMargin},{y},0,3,1,1,N,\"İrsaliye: {EplData(p.IrsaliyeNo)}\"");
            y += 26;
        }

        lines.Add($"LO{EplLeftMargin},{y},{EplContentWidth},3");
        y += 12;

        // ── Gönderici ────────────────────────────────────────────────────
        lines.Add($"A{EplLeftMargin},{y},0,3,1,1,N,\"GÖNDERİCİ: Akyıldız Lojistik A.Ş.\"");
        lines.Add("P1");

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// Koli etiketi — EPL2 karşılığı. 100 x 80 mm (800 x 640 dot @ 203 dpi).
    /// ZPL sürümü gibi üç ortalanmış satır: proje adı · lokasyon · proje kodu (koli adedi).
    /// EPL'de otomatik ortalama yok; x koordinatı font genişliğinden hesaplanıyor ve
    /// metin taşarsa punto küçültülüyor — satır hiçbir zaman kırpılmaz.
    /// </summary>
    private static string BuildBoxEpl(PendingJob job)
    {
        var p = JsonSerializer.Deserialize<BoxLabelPayload>(job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Koli payload parse edilemedi.");

        var name = EplTruncate(p.ProjectName.ToUpper(TurkishCulture), 18);
        var loc  = EplTruncate(p.Location.ToUpper(TurkishCulture),    18);
        var code = $"{p.ProjectCode} ({p.BoxCount} KOLİ)";

        var lines = new List<string>
        {
            "N",
            "q800",
            EplCharsetLine,
            // Üst sınırlar ZPL sürümündeki punto hiyerarşisini korur (80 / 60 / 50):
            // sınır olmasaydı kısa bir lokasyon adı proje adından büyük basılırdı.
            EplCenteredLine(name, 110, 96),
            EplCenteredLine(loc,  250, 72),
            EplCenteredLine(code, 390, 48),
            "P1",
        };

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// EPL metin kodlaması — DOS 857 (Türkçe). Kâğıt üzerinde doğrulandı (2026-08-17):
    /// <c>I8,6,000</c> + CP857 baytlarıyla ÇĞİÖŞÜ/çğıöşü doğru bastı.
    ///
    /// .NET 10'da bu kod sayfası ancak <c>System.Text.Encoding.CodePages</c> sağlayıcısı
    /// kaydedilirse gelir; kayıt yapılmadan <c>GetEncoding</c> fırlatır. Canlıdaki eski
    /// "No data is available for encoding 1254" hatalarının sebebi tam olarak buydu —
    /// o yüzden burada sessizce patlamak yerine null'a düşüp ASCII'ye geriliyoruz.
    /// </summary>
    private static readonly Encoding? EplEncoding = ResolveEplEncoding();

    private static Encoding? ResolveEplEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(857);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// EPL karakter seti satırı. Kod sayfası yüklenemediyse cihazı varsayılan
    /// DOS 437'de bırakır ve metin ASCII'ye indirgenerek gönderilir (bkz. SendRawToPrinter).
    /// </summary>
    private static string EplCharsetLine => EplEncoding is not null ? "I8,6,000" : "I8,0,000";

    /// <summary>
    /// EPL etiketlerinde içeriğin başladığı üst kenar payı (dot, 203 dpi'de ~14 mm).
    /// Sahadaki rulo Yurtiçi Kargo baskılı ve bu kalıcı: üst bantta basılı logo var,
    /// içerik y=0'dan başlarsa onun üstüne biniyor (2026-08-17 palet baskısında görüldü).
    /// </summary>
    private const int EplTopMargin = 110;

    private const int EplLabelWidth   = 800;   // 100 mm @ 203 dpi
    private const int EplLeftMargin   = 40;
    private const int EplContentWidth = 720;   // 800 − 40 sol − 40 sağ

    /// <summary>
    /// Bir karakterin yatay ilerlemesi. EPL font hücresine <b>2 dot</b> karakter arası
    /// boşluk ekler ve bu da çarpanla büyür — hesaba katılmazsa metin sığıyor sanılır
    /// ama kâğıtta taşar. 2026-08-17'de iki kez bu yüzden kırpıldı: koli etiketinde
    /// satır soldan/sağdan kesildi, kargoda gönderici satırı "YURTİÇİ KARGO"nun üstüne bindi.
    /// </summary>
    private static int EplAdvance(int cellWidth, int mult) => (cellWidth + 2) * mult;

    /// <summary>
    /// (font, çarpan) merdiveni — büyükten küçüğe. EPL'de font hücreleri sabit olduğu için
    /// tek bir fontu çarpanla büyütmek kaba adımlar veriyor (48 → 96 → 144 dot); araya
    /// font 4 ve 3 girince ölçek yumuşuyor ve uzun metinler gereksiz yere küçülmüyor.
    /// Değerler: (font, çarpan, karakter ilerlemesi, hücre yüksekliği) — genişlik
    /// <see cref="EplAdvance"/> ile hesaplanmış hâlde tutuluyor.
    /// </summary>
    private static readonly (int Font, int Mult, int W, int H)[] EplFontLadder =
    [
        (5, 2, 68, 96),
        (4, 3, 48, 72),
        (5, 1, 34, 48),
        (4, 2, 32, 48),
        (3, 2, 28, 40),
        (3, 1, 12, 20),
    ];

    /// <summary>
    /// Ortalanmış tek satır. <paramref name="maxHeight"/> punto hiyerarşisini korur
    /// (proje adı > lokasyon > kod), genişlik bütçesi ise kırpılmayı önler.
    ///
    /// Bütçe <see cref="EplContentWidth"/>: her yana 40 dot pay bırakır. Merdivendeki
    /// genişlikler karakter arası boşluğu içeriyor (bkz. <see cref="EplAdvance"/>) —
    /// eksik model yüzünden 2026-08-17'de satır kâğıtta kırpılmıştı.
    /// </summary>
    private static string EplCenteredLine(string text, int y, int maxHeight)
    {
        var pick = EplFontLadder.FirstOrDefault(
            f => f.H <= maxHeight && text.Length * f.W <= EplContentWidth,
            EplFontLadder[^1]);

        int x = Math.Max(EplLeftMargin, (EplLabelWidth - text.Length * pick.W) / 2);
        return $"A{x},{y},0,{pick.Font},{pick.Mult},{pick.Mult},N,\"{EplData(text)}\"";
    }

    /// <summary>
    /// Uzun metni kelime sınırından satırlara böler. Adres gibi alanlar tek satıra
    /// sığmıyor ve ortadan kesilince sokak bilgisi kayboluyor.
    /// <paramref name="maxLines"/> aşılırsa son satır "…" yerine ".." ile biter (ASCII).
    /// </summary>
    private static List<string> WrapText(string? text, int lineLength, int maxLines)
    {
        var result = new List<string>();
        var clean = (text ?? string.Empty).Trim();
        if (clean.Length == 0) return result;

        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();

        foreach (var word in words)
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > lineLength)
            {
                result.Add(current.ToString());
                current.Clear();
                if (result.Count == maxLines) break;
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }

        if (result.Count < maxLines && current.Length > 0)
            result.Add(current.ToString());

        // Sığmayan kısım kaldıysa son satırın devamı olduğunu belli et.
        var used = result.Sum(l => l.Length + 1);
        if (used < clean.Length && result.Count > 0)
        {
            var last = result[^1];
            result[^1] = last.Length > lineLength - 2 ? last[..(lineLength - 2)] + ".." : last + "..";
        }

        return result;
    }

    /// <summary>
    /// Çok kolili gönderide "  Parça: 2/3" eki. Tek parçada hiçbir şey eklemez —
    /// her gönderiye "1/1" yazmak etiketi gereksiz kalabalıklaştırır.
    /// </summary>
    private static string PieceSuffix(CargoLabelPayload p) =>
        p.PieceTotal > 1 ? $"  Parca: {p.PieceNo}/{p.PieceTotal}" : string.Empty;

    /// <summary>Türkçe harfleri ASCII karşılığına indirger — EPL kod sayfası tuzağını atlar.</summary>
    private static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch switch
            {
                'Ç' => "C", 'ç' => "c",
                'Ğ' => "G", 'ğ' => "g",
                'İ' => "I", 'ı' => "i",
                'Ö' => "O", 'ö' => "o",
                'Ş' => "S", 'ş' => "s",
                'Ü' => "U", 'ü' => "u",
                _   => ch.ToString(),
            });
        return sb.ToString();
    }

    /// <summary>EPL veri alanı kaçışı — tırnak ve ters bölü komutu bozar.</summary>
    private static string EplData(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>EPL için kısaltma — ZPL'de kullanılan '…' karakteri ASCII değil.</summary>
    private static string EplTruncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 2)] + "..";

    // ── Win32 Raw Print ───────────────────────────────────────────────────────
 
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName  = "EtiketJob";
        [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType = "RAW";
    }
 
    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA",     SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string sz, out IntPtr h, IntPtr pd);
 
    [DllImport("winspool.Drv", EntryPoint = "ClosePrinter",     SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool ClosePrinter(IntPtr h);
 
    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool StartDocPrinter(IntPtr h, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);
 
    [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter",    SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool EndDocPrinter(IntPtr h);
 
    [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool StartPagePrinter(IntPtr h);
 
    [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter",   SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool EndPagePrinter(IntPtr h);
 
    [DllImport("winspool.Drv", EntryPoint = "WritePrinter",     SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool WritePrinter(IntPtr h, IntPtr pBytes, int dwCount, out int dwWritten);
 
    /// <summary>
    /// Ham etiket verisini (ZPL ya da EPL) Windows spooler'ına RAW olarak yollar.
    /// DİKKAT: bu çağrının başarılı dönmesi yalnızca "baytlar spooler'a teslim edildi"
    /// demektir — cihazın basıp basmadığını söylemez. Yanlış dil gönderilirse iş
    /// arayüzde "Tamamlandı" görünür ama kâğıt çıkmaz.
    /// </summary>
    private static void SendRawToPrinter(string printerName, string data, bool isEpl)
    {
        // ZPL → UTF-8 (^CI28 ile eşleşir); Türkçe karakterleri Font 0 ile doğru basar.
        // EPL → DOS 857 (etiketteki I8,6,000 satırıyla eşleşir).
        // Kod sayfası sağlayıcısı yüklenemediyse EPL metni ASCII'ye indirgenir: etiket
        // Türkçe harfsiz ama okunur çıkar. Sessizce bozuk karakter basmaktan iyidir.
        byte[] bytes =
            !isEpl                       ? Encoding.UTF8.GetBytes(data)
            : EplEncoding is not null    ? EplEncoding.GetBytes(data)
            :                              Encoding.ASCII.GetBytes(Ascii(data));
 
        if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
            throw new InvalidOperationException($"Yazıcı açılamadı: {printerName} (Hata: {Marshal.GetLastWin32Error()})");
 
        try
        {
            var di = new DOCINFOA();
            if (!StartDocPrinter(hPrinter, 1, di))
                throw new InvalidOperationException($"StartDocPrinter başarısız (Hata: {Marshal.GetLastWin32Error()})");
 
            StartPagePrinter(hPrinter);
 
            var pBytes = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, pBytes, bytes.Length);
                if (!WritePrinter(hPrinter, pBytes, bytes.Length, out _))
                    throw new InvalidOperationException($"WritePrinter başarısız (Hata: {Marshal.GetLastWin32Error()})");
            }
            finally
            {
                Marshal.FreeCoTaskMem(pBytes);
            }
 
            EndPagePrinter(hPrinter);
            EndDocPrinter(hPrinter);
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }
 
    // ── Helpers ───────────────────────────────────────────────────────────────
 
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
 
    private static int CenterX(string text, int labelWidth, int charWidth) =>
        Math.Max(0, (labelWidth - text.Length * charWidth) / 2);

    /// <summary>
    /// Code128 baskı genişliği (dot). Subset B varsayımı: veri karakteri başına 11 modül,
    /// artı start (11) + checksum (11) + stop (13). Üst sınır verir — ortalama için yeterli.
    /// </summary>
    private static int Code128Width(string data, int moduleWidth) =>
        (11 * (data.Length + 2) + 13) * moduleWidth;

    /// <summary>Tam sayıysa ondalık gösterme: 120 → "120", 12,5 → "12,5".</summary>
    private static string FormatQty(decimal qty) =>
        qty == Math.Floor(qty) ? ((long)qty).ToString() : qty.ToString("0.##");

    /// <summary>
    /// Verilen punto merdiveninden metnin <paramref name="maxWidth"/> dot'a sığdığı
    /// en büyüğünü seçer; hiçbiri sığmazsa en küçüğünü döner (metin kırpılmaz).
    /// Font 0 için karakter genişliği ≈ punto x 0,74 (mevcut etiketlerin oranı).
    /// </summary>
    private static (int Font, int CharWidth) FitFont(string text, int maxWidth, params int[] ladder)
    {
        foreach (var font in ladder)
        {
            int charWidth = (int)Math.Round(font * 0.74);
            if (text.Length * charWidth <= maxWidth) return (font, charWidth);
        }
        int smallest = ladder[^1];
        return (smallest, (int)Math.Round(smallest * 0.74));
    }
}
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
/// <summary>
/// <c>Desi</c>: bu kolinin desisi (gönderi toplamı değil); 0 ise etikette yazılmaz.
/// <c>JobId</c>: Yurtiçi iş numarası — kargocu bunu da okutuyor, ikinci barkod olarak basılır.
/// </summary>
public record CargoLabelPayload(string Barcode, string ReceiverName, string Address, string Phone, string? IrsaliyeNo, int ShipmentId, string DeliveryDate, int PieceNo = 1, int PieceTotal = 1, string? OrderNo = null, decimal Desi = 0m, int? JobId = null);
/// <summary>
/// <c>BoxNo</c>: kaçıncı koli ("3 / 5"). <c>Barcode</c>: o koliye özel barkod — boşsa
/// (eski API sürümü) barkod alanı basılmaz, etiket eski gibi çalışır.
/// </summary>
public record BoxLabelPayload(string ProjectName, string Location, string ProjectCode, int BoxCount, int BoxNo = 1, string? Barcode = null);
/// <summary>
/// <c>KapTipi</c>: doluysa ("ARABA" / "PALET") bu bir <b>toplama kabı</b> etiketidir ve
/// ayrı yerleşimle basılır — kap kimliği taşır, içerik değil. Boş/eksikse mal kabul
/// palet etiketi olarak basılır (tarih · özet · tedarikçi). Eski API sürümü bu alanı
/// göndermezse null kalır ve davranış eskisiyle aynı olur.
/// </summary>
public record PalletLabelPayload(string PalletCode, string ReceivedDate, string? SupplierName, int LineCount, decimal TotalQty, string? KapTipi = null);
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

        // Etiket şablonları static; tek örnekli servis olduğu için ayar burada alınıyor.
        BoxCodeBadgeFilled = _opts.KoliRozetDolgu;

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
 
        // Yerleşim tamamen SABİT koordinatlı. Eski sürüm akış tabanlıydı (y += ...):
        // adres iki yerine üç satır gelince alttaki her şey kayıyor, aynı şablon iki
        // farklı görünümde basılıyordu. Sabit düzende boş alan boş kalır, komşusu kaymaz.
        // Alıcı adı = "proje kodu - proje adı"; TAMAMI basılmalı, sığmazsa punto düşer.
        var (name, nameH, nameStep) =
            FitTextBlock(p.ReceiverName.ToUpper(TurkishCulture), CgoContentW, ZplAdvanceFor,
                         CgoNameTiers, CgoNameWrap);
        // 24 idi, aynı taşma sınıfı: CgoContentW/ZplCharW(48)=17 gerçek sınır.
        var phone   = Truncate(p.Phone, 17);
        var address = WrapText(p.Address, CgoAddrWrap, CgoAddrLines);

        var sb = new StringBuilder();
        sb.Append("^XA\n^CI28\n^PW800\n^LL640\n");

        // ── Alıcı ────────────────────────────────────────────────────────
        for (int i = 0; i < name.Count; i++)
            sb.Append($"^FO{EplLeftMargin},{CgoNameY1 + i * nameStep}^A0N,{nameH},{nameH}^FD{name[i]}^FS\n");

        for (int i = 0; i < address.Count && i < CgoAddrLines; i++)
            sb.Append($"^FO{EplLeftMargin},{CgoAddrY1 + i * CgoAddrStep}^A0N,{CgoAddrH},{CgoAddrH}^FD{address[i]}^FS\n");

        if (phone.Length > 0)
            sb.Append($"^FO{EplLeftMargin},{CgoPhoneY}^A0N,{CgoPhoneH},{CgoPhoneH}^FD{phone}^FS\n");

        sb.Append($"^FO{EplLeftMargin},{CgoDividerY}^GB{CgoContentW},{MinRuleThickness},{MinRuleThickness}^FS\n");

        // ── Desi / parça kutusu ──────────────────────────────────────────
        // Çerçeve, dolgu değil: geniş siyah alan direkt termalde leke yapıyor ve
        // etiket aylarca kutunun üstünde kalıyor.
        var desi = CargoDesiLine(p);
        sb.Append($"^FO{EplLeftMargin},{CgoDesiBoxY}^GB{CgoDesiBoxW},{CgoDesiBoxH},{MinRuleThickness}^FS\n");
        int desiX = EplLeftMargin + Math.Max(0, (CgoDesiBoxW - desi.Length * ZplCharW(CgoDesiH)) / 2);
        sb.Append($"^FO{desiX},{CgoDesiTextY}^A0N,{CgoDesiH},{CgoDesiH}^FD{desi}^FS\n");

        // ── Künye ────────────────────────────────────────────────────────
        sb.Append($"^FO{CgoKunyeX},{CgoKunyeY1}^A0N,{CgoKunyeH},{CgoKunyeH}^FD{CargoKunyeLine(p, CgoKunyeWrap)}^FS\n");
        var ref2 = CargoRefLine(p);
        if (ref2.Length > 0)
            sb.Append($"^FO{CgoKunyeX},{CgoKunyeY2}^A0N,{CgoKunyeH},{CgoKunyeH}^FD{ref2}^FS\n");

        sb.Append($"^FO{EplLeftMargin},{CgoSenderY}^A0N,{CgoSenderH},{CgoSenderH}^FDGÖNDERİCİ: Akyıldız Lojistik^FS\n");

        // ── Gönderi barkodu ──────────────────────────────────────────────
        var zplCode = (p.Barcode ?? string.Empty).Trim();
        if (zplCode.Length > 0)
        {
            int module = CgoBarcodeMod;
            while (module > 2 && Code128Width(zplCode, module) > CgoContentW) module--;
            int barX = EplLeftMargin + Math.Max(0, (CgoContentW - Code128Width(zplCode, module)) / 2);

            sb.Append($"^FO{barX},{CgoBarcodeY}^BY{module}^BCN,{CgoBarcodeH},N,N,N^FD{zplCode}^FS\n");
            int capX = EplLeftMargin + Math.Max(0, (CgoContentW - zplCode.Length * ZplCharW(Pt24)) / 2);
            sb.Append($"^FO{capX},{CgoBarTextY}^A0N,{Pt24},{Pt24}^FD{zplCode}^FS\n");
        }

        // ── İş numarası — sağ kenarda DİKEY ──────────────────────────────
        // Yan yana iki yatay barkodda okuyucunun tarama çizgisi ikisini birden kesip
        // komşuyu okuyordu (saha, 2026-08-19). Dik barkodu yatay çizgi çözemez.
        var zplJobNo = p.JobId is > 0 ? p.JobId.Value.ToString() : string.Empty;
        if (zplJobNo.Length > 0)
        {
            sb.Append($"^FO{CgoSepX},{EplTopMargin}^GB{CgoSepW},{CgoSepH},{CgoSepW}^FS\n");

            int jobLen = Code128Width(zplJobNo, CgoJobMod);
            int jobY   = CgoJobY + Math.Max(0, (CgoJobLen - jobLen) / 2);

            sb.Append($"^FO{CgoJobX},{jobY}^BY{CgoJobMod}^BCR,{CgoJobBarW},N,N,N^FD{zplJobNo}^FS\n");
            sb.Append($"^FO{CgoJobX + CgoJobBarW + 4},{jobY}^A0R,{Pt24},{Pt24}^FD{zplJobNo}^FS\n");
        }

        sb.Append("^XZ");
        return sb.ToString();
    }

    /// <summary>
    /// Desi/parça kutusunun metni. Parça bilgisi "2/5" biçiminde — kaçıncı koli olduğu
    /// teslimde eksik koli kontrolünü kolaylaştırıyor. İkisi de yoksa kutu boş kalmasın diye tire.
    /// </summary>
    private static string CargoDesiLine(CargoLabelPayload p)
    {
        var parts = new List<string>(2);
        if (p.PieceTotal > 1)   parts.Add($"{p.PieceNo}/{p.PieceTotal}");
        else if (p.PieceNo > 0) parts.Add("1");
        if (p.Desi > 0)         parts.Add($"{FormatQty(p.Desi)} DESI");
        return parts.Count > 0 ? Truncate(string.Join(" - ", parts), CgoDesiWrap) : "-";
    }

    /// <summary>
    /// Künyenin ikinci satırı: sipariş ya da irsaliye. 17 karaktere sığdığı için
    /// ikisi birden yazılamaz — irsaliye tercih edilir, kargocunun aradığı numara odur.
    /// İkisi de boşsa satır hiç basılmaz.
    /// Ön ek 2 karakter ("İ:" / "S:"): Netsis e-irsaliye numarası 15 hane
    /// (AKI202600013479), "İrs " ile 19 olup son iki hanesi kesiliyordu (saha, 2026-09-09).
    /// </summary>
    private static string CargoRefLine(CargoLabelPayload p)
    {
        if (!string.IsNullOrWhiteSpace(p.IrsaliyeNo)) return Truncate($"İ:{p.IrsaliyeNo!.Trim()}", CgoKunyeWrap);
        if (!string.IsNullOrWhiteSpace(p.OrderNo))    return Truncate($"S:{p.OrderNo!.Trim()}", CgoKunyeWrap);
        return string.Empty;
    }
 
    /// <summary>
    /// Toplama kabı (araba / palet) etiketi — ZPL karşılığı. EPL sürümüyle aynı yerleşim;
    /// tek fark punto ölçüsü (font 0 ölçeklenebilir, karakter ≈ punto x 0,74).
    /// </summary>
    private static string BuildContainerZpl(PalletLabelPayload p)
    {
        var code = p.PalletCode.Trim().ToUpperInvariant();
        var tip  = (p.KapTipi ?? string.Empty).Trim().ToUpper(TurkishCulture);

        var sb = new StringBuilder();
        sb.Append("^XA\n^CI28\n^PW800\n^LL640\n");

        // ── Tip kutusu ────────────────────────────────────────────────────
        // Kutu 260 dot'ta SABİT (tasarım referansı) — PALET/ARABA ikisi de bu
        // genişliğe rahatça sığıyor; dinamik büyüme yalnızca beklenmeyen uzun bir
        // tip etiketi gelirse devreye giriyor (ZplCodeCharW ile artık tetiklenmiyor).
        var spaced = string.Join(" ", tip.ToCharArray());
        int tipAdv = ZplCodeCharW(48);
        int tipW   = spaced.Length * tipAdv;
        int boxW   = Math.Max(CntTypeBoxW, tipW + 24);
        // Çerçeve her zaman CntFrameT (4 dot) — PALET için iki katına çıkarma
        // tasarım referansında yok, kaldırıldı (2026-08-26).
        int frameT = CntFrameT;

        if (tip.Length > 0)
        {
            sb.Append($"^FO{EplLeftMargin},{CntTypeBoxY}^GB{boxW},{CntTypeBoxH},{frameT}^FS\n");
            int tipX = EplLeftMargin + Math.Max(0, (boxW - tipW) / 2);
            sb.Append($"^FO{tipX},{CntTypeY}^A0N,48,48^FD{spaced}^FS\n");
        }

        // ── Kap kodu — sığan en büyük punto ───────────────────────────────
        // Punto kademeli düşer: 144 → 96 → 72 → 48. Eskiden 24'er azalıyordu ve
        // 120 gibi kademe dışı değerler üretiyordu; EPL karşılığı olmayan punto
        // yazıcıda en yakın alta yuvarlanıp iki dilin yerleşimini ayrıştırıyor.
        int codeH = Pt144;
        while (codeH > Pt48 && code.Length * ZplCodeCharW(codeH) > EplContentWidth)
            codeH = codeH == Pt144 ? Pt96 : codeH == Pt96 ? Pt72 : Pt48;
        sb.Append($"^FO{EplLeftMargin},{CntCodeY}^A0N,{codeH},{codeH}^FD{code}^FS\n");

        // ── Kalın ayırıcı çizgi ───────────────────────────────────────────
        sb.Append($"^FO{EplLeftMargin},{CntDividerY}^GB{EplContentWidth},{CntDividerH},{CntDividerH}^FS\n");

        // ── Barkod ────────────────────────────────────────────────────────
        int module = CntBarcodeMod;
        while (module > 2 && CntBarcodeX + Code128Width(code, module) > BoxRightEdge) module--;
        sb.Append($"^FO{CntBarcodeX},{CntBarcodeY}^BY{module}^BCN,{CntBarcodeH},N,N,N^FD{code}^FS\n");

        int textX = Math.Max(EplLeftMargin, (EplLabelWidth - code.Length * tipAdv) / 2);
        sb.Append($"^FO{textX},{CntBarTextY}^A0N,48,48^FD{code}^FS\n");

        sb.Append("^XZ");
        return sb.ToString();
    }

    private static string BuildBoxZpl(PendingJob job)
    {
        var p = JsonSerializer.Deserialize<BoxLabelPayload>(job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Koli payload parse edilemedi.");
 
        // EPL sürümüyle AYNI yerleşim — sabitler tek yerden (Box* alanları) okunuyor,
        // iki dil arasında kayma olmasın. Tek fark punto ölçüsü: ZPL'de font 0
        // ölçeklenebilir ve karakter genişliği ≈ punto x 0,74 (bkz. FitFont).
        var name = p.ProjectName.ToUpper(TurkishCulture).Trim();
        // 30 idi, aynı taşma sınıfı: EplContentWidth/ZplCharW(48)=20 gerçek sınır.
        var loc  = Truncate(p.Location.ToUpper(TurkishCulture), 20);
        var code = p.ProjectCode.Trim().ToUpperInvariant();
        var seq  = $"KOLI {p.BoxNo} / {p.BoxCount}";
        var bar  = (p.Barcode ?? string.Empty).Trim();

        // Proje kodu rakam — 0,74 oranı harf/adres metni için, rakam ZPL font 0'da
        // belirgin daha dar basıyor (bkz. ZplCodeCharW). Yanlış oranla rozet 6 haneli
        // kodda gereğinden büyük hesaplanıyordu (bkz. tasarım referansı, 2026-08-26).
        int zAdv72 = ZplCodeCharW(72);
        int zAdv48 = (int)Math.Round(48 * 0.74);   // 36
        int zAdv32 = (int)Math.Round(32 * 0.74);   // 24

        var sb = new StringBuilder();
        sb.Append("^XA\n^CI28\n^PW800\n^LL640\n");

        // ── Proje adı — TAMAMI basılır; sığmazsa punto düşüp satır artar ──
        var (nameLines, nameH, nameStep) =
            FitTextBlock(name, EplContentWidth, ZplAdvanceFor, BoxNameTiers, BoxNameWrap);

        for (int i = 0; i < nameLines.Count; i++)
        {
            int ly = BoxNameY1 + i * nameStep;
            sb.Append($"^FO{EplLeftMargin},{ly}^A0N,{nameH},{nameH}^FD{nameLines[i]}^FS\n");
        }

        // ── Birim — boşsa satır hiç basılmaz, altındaki her şey yukarı kayar ──
        int shift = BoxNoUnitShift;
        if (loc.Length > 0)
        {
            shift = 0;
            sb.Append($"^FO{EplLeftMargin},{BoxUnitY}^A0N,48,48^FD{loc}^FS\n");
        }

        // ── Ayırıcı çizgi ─────────────────────────────────────────────────
        sb.Append($"^FO{EplLeftMargin},{BoxDividerY - shift}^GB{EplContentWidth},{BoxDividerH},{BoxDividerH}^FS\n");

        // ── Proje kodu — ters rozet ───────────────────────────────────────
        int badgeW = code.Length >= 6 ? BoxBadgeW6 : BoxBadgeW;
        int codeW  = code.Length * zAdv72;
        if (codeW + BoxBadgePad > badgeW) badgeW = codeW + BoxBadgePad;

        int badgeY = BoxBadgeY - shift;
        int codeX  = EplLeftMargin + Math.Max(0, (badgeW - codeW) / 2);

        if (BoxCodeBadgeFilled)
        {
            // ^GB'de kalınlık = yükseklik → içi dolu dikdörtgen. ^FR alanı ters çevirir.
            sb.Append($"^FO{EplLeftMargin},{badgeY}^GB{badgeW},{BoxBadgeH},{BoxBadgeH}^FS\n");
            sb.Append($"^FO{codeX},{BoxCodeY - shift}^FR^A0N,72,72^FD{code}^FS\n");
        }
        else
        {
            sb.Append($"^FO{EplLeftMargin},{badgeY}^GB{badgeW},{BoxBadgeH},{BoxDividerH}^FS\n");
            sb.Append($"^FO{codeX},{BoxCodeY - shift}^A0N,72,72^FD{code}^FS\n");
        }

        // ── Koli sırası — sağa dayalı, rozetin karşısında ──────────────────
        int seqX = Math.Max(EplLeftMargin + badgeW + 20, BoxRightEdge - seq.Length * zAdv48);
        sb.Append($"^FO{seqX},{BoxSeqY - shift}^A0N,48,48^FD{seq}^FS\n");

        // ── Barkod ────────────────────────────────────────────────────────
        if (bar.Length > 0)
        {
            int module = BoxBarcodeModule;
            while (module > 2 && BoxBarcodeX + Code128Width(bar, module) > BoxRightEdge) module--;

            sb.Append($"^FO{BoxBarcodeX},{BoxBarcodeY - shift}^BY{module}^BCN,{BoxBarcodeH},N,N,N^FD{bar}^FS\n");

            int textX = Math.Max(EplLeftMargin, (EplLabelWidth - bar.Length * zAdv32) / 2);
            sb.Append($"^FO{textX},{BoxBarTextY - shift}^A0N,32,32^FD{bar}^FS\n");
        }

        sb.Append("^XZ");
        return sb.ToString();
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

        // Kap tipi doluysa bu bir toplama kabı etiketi — ayrı yerleşim.
        if (!string.IsNullOrWhiteSpace(p.KapTipi)) return BuildContainerZpl(p);

        var code    = p.PalletCode.Trim().ToUpperInvariant();
        var summary = $"{p.LineCount} KALEM - {FormatQty(p.TotalQty)} ADET";

        // Tedarikçi adı Türkçe büyük harf: ToUpperInvariant "i" → "I" verir, doğrusu "İ".
        // ^CI28 + UTF-8 zaten Türkçe karakterleri basıyor (bkz. SendZplToPrinter notu).
        // Opsiyonel satırlar 48 punto: ZPL'de karakter 36 dot ilerler, 720 dot'luk
        // içerik alanına 20 karakter sığar. 32'de satır kâğıttan taşıyordu.
        var supplierRaw = (p.SupplierName ?? string.Empty).Trim();
        var supplier    = supplierRaw.Length == 0
            ? string.Empty
            : Truncate(supplierRaw.ToUpper(TurkishCulture), EplContentWidth / ZplCharW(PalOptH));

        // Kod puntosu kademelidir: 96 sığmazsa 72, o da sığmazsa 48. Ara değer yok —
        // iki dil aynı yerleşimi paylaştığı için serbest punto satır yüksekliklerini kaydırır.
        int codeH = PalCodeH;
        while (codeH > Pt48 && code.Length * ZplCharW(codeH) > EplContentWidth)
            codeH = codeH == Pt96 ? Pt72 : Pt48;

        var sb = new StringBuilder();
        sb.Append("^XA\n^CI28\n^PW800\n^LL640\n");

        sb.Append($"^FO{EplLeftMargin},{PalCodeY}^A0N,{codeH},{codeH}^FD{code}^FS\n");
        sb.Append($"^FO{EplLeftMargin},{PalDividerY}^GB{EplContentWidth},{MinRuleThickness},{MinRuleThickness}^FS\n");

        // ── Opsiyoneller: ALTA YASLI ──────────────────────────────────────
        // Kaç satır gelirse gelsin blok 400'de biter; barkod ve kod asla kaymaz.
        var opts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(p.ReceivedDate)) opts.Add($"TARIH: {p.ReceivedDate!.Trim()}");
        if (p.LineCount > 0 || p.TotalQty > 0)          opts.Add(summary);
        if (supplier.Length > 0)                        opts.Add(supplier);

        int firstY = PalOptBottom - PalOptH - (opts.Count - 1) * PalOptStep;
        for (int i = 0; i < opts.Count; i++)
        {
            int oy = firstY + i * PalOptStep;
            sb.Append($"^FO{ZplCenterX(opts[i], PalOptH)},{oy}^A0N,{PalOptH},{PalOptH}^FD{opts[i]}^FS\n");
        }

        // ── Barkod: konumu SABİT ──────────────────────────────────────────
        int module = PalBarcodeMod;
        while (module > 2 && Code128Width(code, module) > EplContentWidth) module--;
        int barX = Math.Max(EplLeftMargin, (EplLabelWidth - Code128Width(code, module)) / 2);

        sb.Append($"^FO{barX},{PalBarcodeY}^BY{module}^BCN,{PalBarcodeH},N,N,N^FD{code}^FS\n");
        sb.Append($"^FO{ZplCenterX(code, Pt32)},{PalBarTextY}^A0N,{Pt32},{Pt32}^FD{code}^FS\n");
        sb.Append("^XZ");

        return sb.ToString();
    }

    /// <summary>ZPL font 0'da bir karakterin yatay ilerlemesi ≈ punto x 0,74.</summary>
    private static int ZplCharW(int height) => (int)Math.Round(height * 0.74);

    /// <summary>
    /// Kısa kod alanları (proje kodu, kap/palet kodu) için oran — 0,74 serbest metin
    /// (proje adı, adres) için ölçülmüş ve rakam/kısa kod içeren alanlarda rozet ve
    /// kutu boyutunu gereğinden büyük hesaplatıyordu (tasarım referansı, 2026-08-26).
    /// Yalnızca bu tür kısa alanlarda kullanılır — serbest metin sarma mantığı
    /// (ZplCharW / ZplAdvanceFor) kasıtlı olarak dokunulmadan bırakıldı.
    /// </summary>
    private static int ZplCodeCharW(int height) => (int)Math.Round(height * 0.5);

    /// <summary>Verilen puntoda metni etiket genişliğinde ortalayan sol kenar.</summary>
    private static int ZplCenterX(string text, int height) =>
        Math.Max(EplLeftMargin, (EplLabelWidth - text.Length * ZplCharW(height)) / 2);
 
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
    /// <summary>EPL'de içi boş dikdörtgen komutu yok — dört kenar ayrı çiziliyor.</summary>
    private static void EplRect(List<string> lines, int x, int y, int w, int h, int t)
    {
        lines.Add($"LO{x},{y},{w},{t}");
        lines.Add($"LO{x},{y + h - t},{w},{t}");
        lines.Add($"LO{x},{y},{t},{h}");
        lines.Add($"LO{x + w - t},{y},{t},{h}");
    }

    /// <summary>
    /// Toplama kabı (araba / palet) etiketi — EPL2. 100 x 80 mm.
    /// Yerleşim: tip kutusu · kap kodu (dev punto) · kalın çizgi · barkod.
    ///
    /// Mal kabul palet etiketinden ayrıldı: orada tarih/tedarikçi/özet var, burada yok.
    /// Eskiden aynı şablon kullanılıyor ve boş alanlar düştüğü için etiket
    /// "palet etiketinin eksik hâli" gibi duruyordu.
    /// </summary>
    private static string BuildContainerEpl(PalletLabelPayload p)
    {
        var code = Ascii(p.PalletCode.Trim().ToUpperInvariant());
        var tip  = (p.KapTipi ?? string.Empty).Trim().ToUpper(TurkishCulture);

        var lines = new List<string> { "N", "q800", EplCharsetLine };

        // ── Tip kutusu ────────────────────────────────────────────────────
        // Harf arası boşluk EPL'de ayarlanamıyor; harfler arasına boşluk konuyor.
        // Kutu 260'ta sabit kalsa boşluklu metin taşardı, o yüzden 260 ASGARİ ölçü.
        var spaced = string.Join(" ", tip.ToCharArray());
        int tipAdv = EplAdvance(14, 2);
        int tipW   = spaced.Length * tipAdv;
        int boxW   = Math.Max(CntTypeBoxW, tipW + 24);
        // Çerçeve her zaman CntFrameT (4 dot) — PALET için iki katına çıkarma
        // tasarım referansında yok, kaldırıldı (2026-08-26).
        int frameT = CntFrameT;

        if (tip.Length > 0)
        {
            EplRect(lines, EplLeftMargin, CntTypeBoxY, boxW, CntTypeBoxH, frameT);
            int tipX = EplLeftMargin + Math.Max(0, (boxW - tipW) / 2);
            lines.Add($"A{tipX},{CntTypeY},0,4,2,2,N,\"{EplData(spaced)}\"");
        }

        // ── Kap kodu — sığan en büyük çarpan ──────────────────────────────
        // Tasarım font 4 x6 istiyor (144 punto, karakter 96 dot). 8 karakter 768 dot
        // eder ve 720'lik alandan taşar; bu yüzden çarpan sığana kadar düşüyor.
        // Kod hiçbir zaman kırpılmaz, küçülür.
        int codeMult = 6;
        while (codeMult > 2 && code.Length * EplAdvance(14, codeMult) > EplContentWidth) codeMult--;
        lines.Add($"A{EplLeftMargin},{CntCodeY},0,4,{codeMult},{codeMult},N,\"{EplData(code)}\"");

        // ── Kalın ayırıcı çizgi ───────────────────────────────────────────
        lines.Add($"LO{EplLeftMargin},{CntDividerY},{EplContentWidth},{CntDividerH}");

        // ── Barkod ────────────────────────────────────────────────────────
        // Tarama hedefi barkodun içeriği = kap kodunun kendisi (ayrıştırma yok).
        int module = CntBarcodeMod;
        while (module > 2 && CntBarcodeX + Code128Width(code, module) > BoxRightEdge) module--;
        lines.Add($"B{CntBarcodeX},{CntBarcodeY},0,3,{module},{module * 2},{CntBarcodeH},N,\"{EplData(code)}\"");

        int textAdv = EplAdvance(14, 2);
        int textX   = Math.Max(EplLeftMargin, (EplLabelWidth - code.Length * textAdv) / 2);
        lines.Add($"A{textX},{CntBarTextY},0,4,2,2,N,\"{EplData(code)}\"");

        lines.Add("P1");
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static string BuildPalletEpl(PendingJob job)
    {
        var p = JsonSerializer.Deserialize<PalletLabelPayload>(job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Palet payload parse edilemedi.");

        if (string.IsNullOrWhiteSpace(p.PalletCode))
            throw new InvalidDataException("Palet kodu boş — etiket basılamaz.");

        // Kap tipi doluysa bu bir toplama kabı etiketi — ayrı yerleşim.
        if (!string.IsNullOrWhiteSpace(p.KapTipi)) return BuildContainerEpl(p);

        // Barkod verisi ASCII kalmalı — Code128'e Türkçe harf girmesin.
        var code = Ascii(p.PalletCode.Trim().ToUpperInvariant());

        // Kod puntosu kademeli: 96 (font 5 x2) sığmazsa 72 (font 4 x3), sonra 48 (font 4 x2).
        // Serbest çarpan yok — ZPL sürümüyle aynı kademeleri kullanmazsa iki dil ayrışır.
        (int font, int mult) = code.Length * EplAdvance(32, 2) <= EplContentWidth ? (5, 2)
                             : code.Length * EplAdvance(14, 3) <= EplContentWidth ? (4, 3)
                             : (4, 2);

        // Code128 dar bar genişliği: sığana kadar 3 → 2.
        int narrow = PalBarcodeMod;
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
            $"A{EplLeftMargin},{PalCodeY},0,{font},{mult},{mult},N,\"{EplData(code)}\"",
            $"LO{EplLeftMargin},{PalDividerY},{EplContentWidth},{MinRuleThickness}",
        };

        // ── Opsiyoneller: ALTA YASLI ──────────────────────────────────────
        // Eski sürümde bu satırlar yukarıdan akıyor, barkod da onların altına
        // kayıyordu: aynı palet tarih varken başka, yokken başka yerde barkod
        // taşıyordu ve depocu her seferinde farklı yerde arıyordu. Artık blok
        // PalOptBottom'da biter, barkodun yeri satır sayısından bağımsızdır.
        var opts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(p.ReceivedDate)) opts.Add($"TARIH: {p.ReceivedDate}");
        if (p.LineCount > 0 || p.TotalQty > 0)          opts.Add(summary);
        if (supplier.Length > 0)                        opts.Add(supplier);

        int firstY = PalOptBottom - PalOptH - (opts.Count - 1) * PalOptStep;
        for (int i = 0; i < opts.Count; i++)
        {
            int ox = Math.Max(EplLeftMargin, (EplLabelWidth - opts[i].Length * EplAdvance(14, 2)) / 2);
            lines.Add($"A{ox},{firstY + i * PalOptStep},0,4,2,2,N,\"{EplData(opts[i])}\"");
        }

        // Tarama hedefi barkodun içeriği = PalletCode'un kendisi (parse yok).
        // Konum SABİT; okunur metin barkodun kendisinden değil ayrı satırdan gelir ki
        // punto kademesi (32) korunsun.
        lines.Add($"B{barX},{PalBarcodeY},0,3,{narrow},{narrow * 2},{PalBarcodeH},N,\"{EplData(code)}\"");
        int codeTextX = Math.Max(EplLeftMargin, (EplLabelWidth - code.Length * EplAdvance(10, 2)) / 2);
        lines.Add($"A{codeTextX},{PalBarTextY},0,2,2,2,N,\"{EplData(code)}\"");
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

        // İş no barkodu sağ kenarda 90° döndürülmüş basılır; içerik alanı bu yüzden
        // 666'da biter ve sarma sınırları (CgoNameWrap / CgoAddrWrap) o genişliğe göre
        // ölçülüdür. Sabit karakter sayısı kullanılırsa kenardan taşar — 2026-08-17'de
        // iki kez böyle kırpıldı.
        var jobNo = p.JobId is > 0 ? Ascii(p.JobId.Value.ToString()) : string.Empty;

        var barcode = Ascii((p.Barcode ?? string.Empty).Trim());   // Code128 verisi ASCII kalmalı
        // Alıcı adı = "proje kodu - proje adı"; TAMAMI basılmalı, sığmazsa punto düşer.
        var (name, nameH, nameStep) =
            FitTextBlock(p.ReceiverName.ToUpper(TurkishCulture), CgoContentW, EplAdvanceFor,
                         CgoNameTiers, CgoNameWrap);
        var (nameFont, nameMult) = EplFontFor(nameH);
        // 24 idi, aynı taşma sınıfı: CgoContentW/ZplCharW(48)=17 gerçek sınır.
        var phone   = EplTruncate(p.Phone, 17);
        var address = WrapText(p.Address, CgoAddrWrap, CgoAddrLines);

        // Yerleşim SABİT koordinatlı — eski sürüm akış tabanlıydı ve adres iki yerine
        // üç satır gelince altındaki her şey kayıyordu. İçerik 40–666 arasında kalır;
        // sağdaki şerit dikey iş no barkoduna ayrılmıştır.
        var lines = new List<string> { "N", "q800", EplCharsetLine };

        // ── Alıcı ────────────────────────────────────────────────────────
        for (int i = 0; i < name.Count; i++)
            lines.Add($"A{EplLeftMargin},{CgoNameY1 + i * nameStep},0,{nameFont},{nameMult},{nameMult},N,\"{EplData(name[i])}\"");

        for (int i = 0; i < address.Count && i < CgoAddrLines; i++)
            lines.Add($"A{EplLeftMargin},{CgoAddrY1 + i * CgoAddrStep},0,2,2,2,N,\"{EplData(address[i])}\"");

        if (phone.Length > 0)
            lines.Add($"A{EplLeftMargin},{CgoPhoneY},0,4,2,2,N,\"{EplData(phone)}\"");

        lines.Add($"LO{EplLeftMargin},{CgoDividerY},{CgoContentW},{MinRuleThickness}");

        // ── Desi / parça kutusu ──────────────────────────────────────────
        // Dört ayrı LO ile çerçeve: EPL'de dolgusuz dikdörtgen komutu yok. Dolgu
        // kullanılmıyor — geniş siyah alan direkt termalde leke yapıyor ve etiket
        // kutunun üstünde aylarca kalıyor.
        var desi = CargoDesiLine(p);
        lines.Add($"LO{EplLeftMargin},{CgoDesiBoxY},{CgoDesiBoxW},{MinRuleThickness}");
        lines.Add($"LO{EplLeftMargin},{CgoDesiBoxY + CgoDesiBoxH - MinRuleThickness},{CgoDesiBoxW},{MinRuleThickness}");
        lines.Add($"LO{EplLeftMargin},{CgoDesiBoxY},{MinRuleThickness},{CgoDesiBoxH}");
        lines.Add($"LO{EplLeftMargin + CgoDesiBoxW - MinRuleThickness},{CgoDesiBoxY},{MinRuleThickness},{CgoDesiBoxH}");

        int desiX = EplLeftMargin + Math.Max(MinRuleThickness,
            (CgoDesiBoxW - desi.Length * EplAdvance(10, 1)) / 2);
        lines.Add($"A{desiX},{CgoDesiTextY},0,2,1,1,N,\"{EplData(desi)}\"");

        // ── Künye ────────────────────────────────────────────────────────
        lines.Add($"A{CgoKunyeX},{CgoKunyeY1},0,2,1,1,N,\"{EplData(CargoKunyeLine(p, CgoKunyeWrap))}\"");
        var ref2 = CargoRefLine(p);
        if (ref2.Length > 0)
            lines.Add($"A{CgoKunyeX},{CgoKunyeY2},0,2,1,1,N,\"{EplData(ref2)}\"");

        lines.Add($"A{EplLeftMargin},{CgoSenderY},0,3,1,1,N,\"GONDERICI: Akyildiz Lojistik\"");

        // ── Gönderi barkodu ──────────────────────────────────────────────
        // Boş barkodda B komutu hata verir; barkodsuz etiket basmak hiç basmamaktan iyidir.
        if (barcode.Length > 0)
        {
            int narrow = CgoBarcodeMod;
            while (narrow > 2 && Code128Width(barcode, narrow) > CgoContentW) narrow--;
            int barX = EplLeftMargin + Math.Max(0, (CgoContentW - Code128Width(barcode, narrow)) / 2);

            lines.Add($"B{barX},{CgoBarcodeY},0,3,{narrow},{narrow * 2},{CgoBarcodeH},N,\"{EplData(barcode)}\"");

            int capX = EplLeftMargin + Math.Max(0, (CgoContentW - barcode.Length * EplAdvance(10, 1)) / 2);
            lines.Add($"A{capX},{CgoBarTextY},0,2,1,1,N,\"{EplData(barcode)}\"");
        }

        // ── İş numarası — sağ kenarda DİKEY ──────────────────────────────
        // Yan yana iki YATAY barkodda okuyucunun tarama çizgisi ikisini birden kesip
        // komşuyu okuyordu (saha, 2026-08-19). Dik barkodu yatay çizgi çözemez.
        // Döndürülmüş alanda origin basılan görüntünün sol-üst köşesidir.
        if (jobNo.Length > 0)
        {
            lines.Add($"LO{CgoSepX},{EplTopMargin},{CgoSepW},{CgoSepH}");

            int jobBarLen = Code128Width(jobNo, CgoJobMod);
            int jobY      = CgoJobY + Math.Max(0, (CgoJobLen - jobBarLen) / 2);

            lines.Add($"B{CgoJobX},{jobY},1,3,{CgoJobMod},{CgoJobMod * 2},{CgoJobBarW},N,\"{EplData(jobNo)}\"");
            lines.Add($"A{CgoJobX + CgoJobBarW + 4},{jobY},1,2,1,1,N,\"{EplData(jobNo)}\"");
        }

        lines.Add("P1");
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// Koli etiketi — EPL2 karşılığı. 100 x 80 mm (800 x 640 dot @ 203 dpi).
    /// Yerleşim: proje kodu (dev punto) · proje adı · birim · "3 / 5" · koli barkodu.
    ///
    /// <b>Her koliye ayrı etiket.</b> Eskiden tek etiket basılıp üzerinde "5 KOLİ" yazıyordu;
    /// koliler birbirinden ayırt edilemediği için palete/araca hangi kolinin girdiği
    /// okutulamıyordu. Barkod içeriği <c>BoxKey</c> biçimidir (4 + sevkiyat + sıra).
    ///
    /// EPL'de otomatik ortalama yok; x koordinatı font genişliğinden hesaplanıyor ve
    /// metin taşarsa punto küçültülüyor — satır hiçbir zaman kırpılmaz.
    /// </summary>
    private static string BuildBoxEpl(PendingJob job)
    {
        var p = JsonSerializer.Deserialize<BoxLabelPayload>(job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Koli payload parse edilemedi.");

        var name = p.ProjectName.ToUpper(TurkishCulture).Trim();
        // 30 idi, aynı taşma sınıfı: EplContentWidth/ZplCharW(48)=20 gerçek sınır.
        var loc  = EplTruncate(p.Location.ToUpper(TurkishCulture), 20);
        var code = Ascii(p.ProjectCode.Trim().ToUpperInvariant());
        var seq  = $"KOLI {p.BoxNo} / {p.BoxCount}";
        var bar  = Ascii((p.Barcode ?? string.Empty).Trim());   // Code128 verisi ASCII kalmalı

        var lines = new List<string> { "N", "q800", EplCharsetLine };

        // ── Proje adı — TAMAMI basılır; sığmazsa punto düşüp satır artar ──
        // Sarma sınırı tasarımda 17 karakter; ama 72 puntoda karakter 48 dot ilerliyor
        // ve 17 karakter 816 dot eder — 720'lik alandan taşar. Bu yüzden sınır ölçüden
        // türetiliyor ve 17 yalnızca ÜST sınır olarak duruyor. Sabit karakter sayısı
        // varsayılırsa metin kenardan kırpılır (2026-08-17'de iki kez oldu).
        var (nameLines, nameH, nameStep) =
            FitTextBlock(name, EplContentWidth, EplAdvanceFor, BoxNameTiers, BoxNameWrap);
        var (nameFont, nameMult) = EplFontFor(nameH);

        for (int i = 0; i < nameLines.Count; i++)
        {
            int ly = BoxNameY1 + i * nameStep;
            lines.Add($"A{EplLeftMargin},{ly},0,{nameFont},{nameMult},{nameMult},N,\"{EplData(nameLines[i])}\"");
        }

        // ── Birim — boşsa satır hiç basılmaz, altındaki her şey yukarı kayar ──
        int shift = BoxNoUnitShift;
        if (loc.Length > 0)
        {
            shift = 0;
            lines.Add($"A{EplLeftMargin},{BoxUnitY},0,4,2,2,N,\"{EplData(loc)}\"");
        }

        // ── Ayırıcı çizgi ─────────────────────────────────────────────────
        lines.Add($"LO{EplLeftMargin},{BoxDividerY - shift},{EplContentWidth},{BoxDividerH}");

        // ── Proje kodu — ters rozet ───────────────────────────────────────
        // Kod artık 96 değil 72 punto; okunurluğu puntodan değil KONTRASTTAN alıyor.
        // Kod ve koli sırası SABİT puntoda (72 / 48) — proje adı gibi küçülmezler.
        int adv72 = EplAdvanceFor(Pt72);
        int adv48 = EplAdvanceFor(Pt48);

        int badgeW = code.Length >= 6 ? BoxBadgeW6 : BoxBadgeW;
        int codeW  = code.Length * adv72;
        if (codeW + BoxBadgePad > badgeW) badgeW = codeW + BoxBadgePad;   // beklenmeyen uzun kod taşmasın

        int badgeY = BoxBadgeY - shift;
        int codeX  = EplLeftMargin + Math.Max(0, (badgeW - codeW) / 2);

        if (BoxCodeBadgeFilled)
        {
            lines.Add($"LO{EplLeftMargin},{badgeY},{badgeW},{BoxBadgeH}");
            lines.Add($"A{codeX},{BoxCodeY - shift},0,4,3,3,R,\"{EplData(code)}\"");
        }
        else
        {
            // 4 dot çerçeve — dört kenar ayrı ayrı, EPL'de içi boş dikdörtgen komutu yok.
            lines.Add($"LO{EplLeftMargin},{badgeY},{badgeW},{BoxDividerH}");
            lines.Add($"LO{EplLeftMargin},{badgeY + BoxBadgeH - BoxDividerH},{badgeW},{BoxDividerH}");
            lines.Add($"LO{EplLeftMargin},{badgeY},{BoxDividerH},{BoxBadgeH}");
            lines.Add($"LO{EplLeftMargin + badgeW - BoxDividerH},{badgeY},{BoxDividerH},{BoxBadgeH}");
            lines.Add($"A{codeX},{BoxCodeY - shift},0,4,3,3,N,\"{EplData(code)}\"");
        }

        // ── Koli sırası — sağa dayalı, rozetin karşısında ──────────────────
        int seqX = Math.Max(EplLeftMargin + badgeW + 20, BoxRightEdge - seq.Length * adv48);
        lines.Add($"A{seqX},{BoxSeqY - shift},0,4,2,2,N,\"{EplData(seq)}\"");

        // ── Barkod ────────────────────────────────────────────────────────
        if (bar.Length > 0)
        {
            int module = BoxBarcodeModule;
            while (module > 2 && BoxBarcodeX + Code128Width(bar, module) > BoxRightEdge) module--;

            lines.Add($"B{BoxBarcodeX},{BoxBarcodeY - shift},0,3,{module},{module * 2},{BoxBarcodeH},N,\"{EplData(bar)}\"");

            int textAdv = EplAdvance(10, 2);   // font 2 x2
            int textX   = Math.Max(EplLeftMargin, (EplLabelWidth - bar.Length * textAdv) / 2);
            lines.Add($"A{textX},{BoxBarTextY - shift},0,2,2,2,N,\"{EplData(bar)}\"");
        }

        lines.Add("P1");
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
    private const int EplLabelHeight  = 640;   //  80 mm @ 203 dpi
    private const int EplLeftMargin   = 40;
    private const int EplContentWidth = 720;   // 800 − 40 sol − 40 sağ

    // ── Koli etiketi yerleşimi (2026-08-19 tasarımı) ──────────────────────────
    //
    // Eski düzende proje kodu 96, koli sırası 72, proje adı 48 puntoydu ve ad uzunsa
    // otomatik küçülüyordu: kod adı eziyordu, oysa teslimde okunan alan ADI. Yeni
    // düzende ad en üstte ve en büyük; kod ters rozet sayesinde küçüldüğü hâlde
    // ayrışıyor. Otomatik küçültme kaldırıldı — yerine iki satır kuralı geldi.
    //
    // Punto kademeleri EPL font hücrelerine bağlı, serbest değil (203 dpi):
    //   72 = font 4 x3 (42x72, karakter 48 dot ilerler)
    //   48 = font 4 x2 (28x48, karakter 32 dot)
    //   32 = font 2 x2 (20x32, karakter 24 dot)
    //
    // Y değerleri EplTopMargin'den (110) başlıyor: koli etiketi de diğerleriyle AYNI
    // Yurtiçi baskılı ruloya basıyor ve üst banttaki logo kalıcı. Eskiden y=40'tan
    // başlıyordu — kargo/palet/ürün etiketlerinden ayrışan tek etiket buydu.
    //
    // 70 dot aşağı kayınca tasarım 640'lık kâğıttan taşıyordu (566 → 636, alt pay 4).
    // Çözüm barkod yüksekliğini 110 → 90 almak: asgari okuma yüksekliği 70, yani hâlâ
    // rahat üstünde. Diğer tüm konumlar tasarımdaki hâliyle korundu.
    private const int BoxNameY1        = EplTopMargin +   0;   // 110
    private const int BoxNameY2        = EplTopMargin +  76;   // 186
    private const int BoxUnitY         = EplTopMargin + 164;   // 274
    private const int BoxDividerY      = EplTopMargin + 228;   // 338
    private const int BoxBadgeY        = EplTopMargin + 246;   // 356
    private const int BoxCodeY         = EplTopMargin + 254;   // 364
    private const int BoxSeqY          = EplTopMargin + 266;   // 376
    private const int BoxBarcodeY      = EplTopMargin + 354;   // 464
    private const int BoxBarTextY      = EplTopMargin + 466;   // 576 → alt kenar 608

    /// <summary>Birim satırı basılmazsa çizgi dahil altındaki her şey bu kadar yukarı kayar.</summary>
    private const int BoxNoUnitShift   =  68;

    private const int BoxDividerH      =   4;
    private const int BoxBadgeH        =  88;
    private const int BoxBadgeW        = 268;
    private const int BoxBadgeW6       = 300;   // 6 haneli proje kodunda
    private const int BoxBadgePad      =  24;   // rozet içi asgari yan boşluk
    private const int BoxBarcodeX      = 170;
    private const int BoxBarcodeH      = 100;   // 464 + 100 = 564; metin 576 + 32 → 608 < 620
    private const int BoxBarcodeModule =   3;
    private const int BoxNameWrap      =  17;   // tasarımın istediği ÜST sınır (en büyük puntoda)

    /// <summary>
    /// Proje adının punto kademeleri — ad TAMAMEN basılsın diye punto düşer, kırpılmaz.
    /// Bant: BoxNameY1 (110) … BoxUnitY (274), yani 164 dot. Hiçbir kademe birim satırına
    /// taşmaz, dolayısıyla altındaki hiçbir alan kaymaz:
    ///   72 x2 → son satır 186+72 = 258 · 48 x3 → 214+48 = 262 · 32 x4 → 218+32 = 250
    /// Kapasite (ZPL): 26 · 60 · 120 karakter. Prod'daki en uzun proje adı ~45.
    /// </summary>
    private static readonly TextTier[] BoxNameTiers =
    {
        new(Pt72, 76, 2),
        new(Pt48, 52, 3),
        new(Pt32, 36, 4),
    };

    /// <summary>Etiketin sağ kenarı — sağa dayalı alanlar buradan geriye hesaplanır.</summary>
    private const int BoxRightEdge     = EplLabelWidth - EplLeftMargin;   // 760

    // ── Toplama kabı (araba / palet) etiketi yerleşimi (2026-08-20 tasarımı) ──
    //
    // Mal kabul palet etiketinden AYRI: bu etiket kabın kimliğini taşıyor, içeriğini
    // değil. Kaba kalıcı olarak yapıştırılıyor ve aylarca üstünde kalıyor — bu yüzden
    // punto büyük, çizgi kalın, ince detay yok. Sadece iki alan olduğu için kod
    // etiketin yarısını kaplayabiliyor.
    private const int CntTypeBoxY   = 110;
    private const int CntTypeBoxH   =  64;
    private const int CntTypeBoxW   = 260;   // asgari — harf arası boşluklu metin sığmazsa büyür
    private const int CntTypeY      = 118;
    private const int CntCodeY      = 192;
    private const int CntDividerY   = 356;
    private const int CntDividerH   =   6;
    private const int CntBarcodeX   = 176;
    private const int CntBarcodeY   = 380;
    private const int CntBarcodeH   = 180;
    private const int CntBarcodeMod =   4;
    private const int CntBarTextY   = 572;   // + 48 → alt kenar 620

    /// <summary>Çerçeve kalınlığı; PALET'te iki katı basılır — iki tip bakışta ayrılsın diye.</summary>
    private const int CntFrameT     =   4;

    // ── Punto kademeleri (2026-08-20) ─────────────────────────────────────────
    //
    // Serbest punto YOK. EPL'de gömülü fontlar hücre çarpanıyla büyüyor, ara değer
    // istendiğinde yazıcı en yakın alta yuvarlıyor ve tasarım kâğıtta kayıyordu.
    // ZPL tarafı ölçeklenebilir font kullansa da aynı kademeler uygulanıyor: iki dil
    // aynı yerleşimi paylaştığı için punto ayrışırsa satır yükseklikleri tutmuyor.
    //
    // 32 = font 2 x2, 48 = font 4 x2, 72 = font 4 x3, 96 = font 5 x2, 144 = font 5 x3.
    private const int Pt24 = 24, Pt32 = 32, Pt48 = 48, Pt72 = 72, Pt96 = 96, Pt144 = 144;

    /// <summary>Çizgi kalınlığı alt sınırı — 3 dot termalde soluk çıkıyor.</summary>
    private const int MinRuleThickness = 4;

    /// <summary>Kullanılabilir alanın alt kenarı; hiçbir öğe bunun altına inmez.</summary>
    private const int UsableBottom = 620;

    // ── Palet etiketi yerleşimi ───────────────────────────────────────────────
    //
    // Opsiyonel satırlar (tarih / içerik özeti / tedarikçi) ALTA YASLI: barkodun ve
    // kodun yeri, kaç satır geldiğinden bağımsız sabit kalır. Eski sürümde barkod
    // "tarih varsa 248, yoksa 190" diye kayıyordu; aynı palet iki farklı görünümde
    // basılıyor, depocu barkodu her seferinde başka yerde arıyordu.
    private const int PalCodeY      = EplTopMargin;        // 110
    private const int PalCodeH      = Pt96;
    private const int PalDividerY   = 222;
    private const int PalOptBottom  = 400;                 // opsiyonel blok burada biter
    private const int PalOptH       = Pt48;
    private const int PalOptStep    = 56;                  // 240 / 296 / 352
    private const int PalBarcodeX   = 170;
    private const int PalBarcodeY   = 440;                 // SABİT
    private const int PalBarcodeH   = 120;
    private const int PalBarcodeMod =   3;
    private const int PalBarTextY   = 572;                 // + 32 → 604

    // ── Kargo etiketi yerleşimi ───────────────────────────────────────────────
    //
    // Sağdaki 94 dot dikey iş no barkoduna ayrılmış; içerik 40–666 arasında kalır.
    // Yan yana iki YATAY barkod okuyucunun tarama çizgisiyle birden kesiliyordu ve
    // komşu barkod okunuyordu (saha, 2026-08-19) — iş no bu yüzden dik basılıyor.
    private const int CgoContentRight = 666;
    private const int CgoContentW     = CgoContentRight - EplLeftMargin;   // 626
    private const int CgoNameY1       = EplTopMargin;      // 110
    private const int CgoNameY2       = 164;
    private const int CgoNameH        = Pt48;
    // Tasarım 22 karakter istiyordu; ZPL'de 48 punto karakteri 36 dot ilerletiyor ve
    // 22 karakter 626 dot'luk içerik alanını 166 dot aşıyor (kâğıtta ölçüldü).
    // İki dilin aynı görünmesi için dar olana (ZPL) göre 17.
    private const int CgoNameWrap     =  17;

    /// <summary>
    /// Alıcı adının punto kademeleri — "kod - proje adı" tek satıra sığmıyor ve
    /// kırpılınca teslimde okunan alan yarım kalıyordu.
    /// Bant: CgoNameY1 (110) … CgoAddrY1 (220), yani 110 dot. Adres satırları kaymaz:
    ///   48 x2 → 164+48 = 212 · 32 x3 → 182+32 = 214
    /// Kapasite: 34 · 78 karakter. "40845 - ISS HAŞERE BAHÇE BAKIM MERKEZ I -BB" = 43.
    /// </summary>
    private static readonly TextTier[] CgoNameTiers =
    {
        new(Pt48, 54, 2),
        new(Pt32, 36, 3),
    };

    private const int CgoAddrY1       = 220;
    private const int CgoAddrStep     =  36;               // 220 / 256 / 292
    private const int CgoAddrH        = Pt32;
    // 31 idi — CgoContentW(626) / ZplCharW(32)=24 dot ≈ 26 karakter gerçek sınır;
    // 31 kâğıtta dikey iş no barkodunun üstüne taşıyordu (saha, 2026-08-27).
    private const int CgoAddrWrap     =  26;
    private const int CgoAddrLines    =   3;
    private const int CgoPhoneY       = 330;
    private const int CgoPhoneH       = Pt48;
    private const int CgoDividerY     = 388;
    private const int CgoDesiBoxY     = 400;
    private const int CgoDesiBoxW     = 300;
    private const int CgoDesiBoxH     =  56;
    private const int CgoDesiTextY    = 408;

    // Tasarım desi kutusunda 40, künyede 32 punto istiyordu; ikisi de kademe dışı /
    // sığmıyor. Kutunun içi 292 dot (300 − 2x4 çerçeve): 32 puntoda yalnızca 12
    // karakter alır, "2/5 PARÇA · 12,5 DESİ" taşar. Künye 360–666 arasında 306 dot;
    // 32 puntoda yine 12 karakter. İkisi de bir kademe altına, 24'e çekildi —
    // kırpmak yerine küçültmek tercih edildi.
    private const int CgoDesiH        = Pt24;
    private const int CgoDesiWrap     =  15;               // 292 / 18 dot
    private const int CgoKunyeX       = 360;
    private const int CgoKunyeY1      = 400;
    private const int CgoKunyeY2      = 436;
    private const int CgoKunyeH       = Pt24;
    private const int CgoKunyeWrap    =  17;               // 306 / 18 dot
    private const int CgoSenderY      = 464;
    private const int CgoSenderH      = Pt24;
    private const int CgoBarcodeX     =  60;
    private const int CgoBarcodeY     = 496;
    private const int CgoBarcodeH     =  90;
    private const int CgoBarcodeMod   =   3;
    private const int CgoBarTextY     = 592;               // + 24 → 616
    private const int CgoSepX         = 666;
    private const int CgoSepW         =   4;
    private const int CgoSepH         = 510;               // 110 → 620
    private const int CgoJobX         = 690;
    private const int CgoJobY         = EplTopMargin;      // 110
    private const int CgoJobMod       =   2;
    private const int CgoJobBarW      =  70;               // döndürülmüş barkodun çubuk yüksekliği
    private const int CgoJobLen       = 440;               // dikey uzunluk bütçesi

    /// <summary>
    /// Proje kodu rozeti: <c>true</c> = siyah dolgu + ters (beyaz) metin,
    /// <c>false</c> = 4 dot çerçeve + normal siyah metin.
    ///
    /// Yedek varyant, direkt termalde geniş siyah alanın leke yapması ihtimaline karşı
    /// duruyor (aynı sebeple palet etiketinde de dolgudan kaçınıldı). Yerleşim iki
    /// varyantta birebir aynı.
    ///
    /// Ayardan okunuyor (<c>Agent:KoliRozetDolgu</c>): leke kâğıt üzerinde, depoda
    /// görülüyor. Derleme sabiti olsaydı varyantı denemek yeni bir yayın gerektirirdi;
    /// böylece appsettings'te çevirip servisi yeniden başlatmak yetiyor.
    /// </summary>
    private static bool BoxCodeBadgeFilled = true;

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
    /// Metni kelime sınırından <b>en fazla <paramref name="maxLines"/></b> satıra böler;
    /// sığmazsa <c>null</c> döner.
    ///
    /// <see cref="WrapText"/>'ten farkı kırpmamasıdır: kırpma gerektiğini söyler ve kararı
    /// çağırana bırakır — çağıran genelde puntoyu düşürüp yeniden dener
    /// (bkz. <see cref="FitTextBlock"/>).
    /// </summary>
    private static List<string>? TryWrapLines(string text, int lineLength, int maxLines)
    {
        if (lineLength < 4 || maxLines < 1) return null;

        var words   = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result  = new List<string>();
        var current = new StringBuilder();

        foreach (var word in words)
        {
            // Tek kelime satıra sığmıyorsa bölmek işe yaramaz — punto düşmeli.
            if (word.Length > lineLength) return null;

            if (current.Length > 0 && current.Length + 1 + word.Length > lineLength)
            {
                result.Add(current.ToString());
                current.Clear();
                if (result.Count == maxLines) return null;   // bir satır daha gerekiyor
            }

            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result.Count <= maxLines ? result : null;
    }

    /// <summary>Bir metin bloğunun punto kademesi: yazı yüksekliği, satır adımı, azami satır sayısı.</summary>
    private readonly record struct TextTier(int FontH, int LineStep, int MaxLines);

    /// <summary>
    /// Metnin <b>tamamını</b> sığdıran ilk kademeyi seçer: en büyük puntoyla başlar,
    /// sığmazsa puntoyu düşürüp satır sayısını artırır. Kırpma (".." / "…") yalnızca
    /// hiçbir kademe yetmediğinde — pratikte olmaz.
    ///
    /// <b>Neden:</b> proje adı 34 karakterde sabit kesiliyordu ve etikette "..." ile
    /// bitiyordu (saha, 2026-08-20). Teslimde okunan alan bu; yarısı basılınca etiketin
    /// işlevi kalmıyor. Kademeler yerleşimdeki BOŞ BANDA göre ölçüldü, alt komşu
    /// satırlar kaymaz — sabit koordinat düzeni korunuyor.
    ///
    /// <paramref name="advance"/> dile göre değişir (ZPL ölçeklenebilir font, EPL hücre);
    /// <paramref name="firstTierMaxChars"/> tasarımın en büyük puntoda istediği üst sınır.
    /// </summary>
    private static (IReadOnlyList<string> Lines, int FontH, int LineStep) FitTextBlock(
        string? text, int contentWidth, Func<int, int> advance,
        IReadOnlyList<TextTier> tiers, int firstTierMaxChars = 0)
    {
        var clean = (text ?? string.Empty).Trim();

        for (var i = 0; i < tiers.Count; i++)
        {
            var tier = tiers[i];
            int maxChars = contentWidth / Math.Max(1, advance(tier.FontH));
            if (i == 0 && firstTierMaxChars > 0) maxChars = Math.Min(maxChars, firstTierMaxChars);

            var wrapped = TryWrapLines(clean, maxChars, tier.MaxLines);
            if (wrapped != null) return (wrapped, tier.FontH, tier.LineStep);
        }

        // Buraya düşmek için adın en küçük puntoda bile sığmaması gerekir (koli etiketinde
        // 120+ karakter). Yine de kırpılmış hâliyle bassın, iş durmasın.
        var last = tiers[^1];
        int lastChars = contentWidth / Math.Max(1, advance(last.FontH));
        return (WrapText(clean, lastChars, last.MaxLines), last.FontH, last.LineStep);
    }

    /// <summary>ZPL: font 0 ölçeklenebilir, karakter ilerlemesi ≈ punto x 0,74.</summary>
    private static int ZplAdvanceFor(int fontH) => ZplCharW(fontH);

    /// <summary>
    /// EPL: punto serbest değil, font hücresine bağlı.
    /// 72 = font 4 x3 (adv 48) · 48 = font 4 x2 (adv 32) · 32 = font 2 x2 (adv 24).
    /// </summary>
    private static int EplAdvanceFor(int fontH) => fontH switch
    {
        Pt72 => EplAdvance(14, 3),
        Pt48 => EplAdvance(14, 2),
        Pt32 => EplAdvance(10, 2),
        _    => EplAdvance(14, 2),
    };

    /// <summary>EPL yazı komutundaki (font, çarpan) çifti — <see cref="EplAdvanceFor"/> ile aynı kademeler.</summary>
    private static (int Font, int Mult) EplFontFor(int fontH) => fontH switch
    {
        Pt72 => (4, 3),
        Pt48 => (4, 2),
        Pt32 => (2, 2),
        _    => (4, 2),
    };

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
    /// <summary>
    /// Kargo etiketinin künye satırı: sevkiyat no · tarih · parça sırası · <b>bu kolinin desisi</b>.
    /// Desi kargo firmasının ücretlendirmesinde kullanılıyor, her koli kendi değerini taşımalı.
    ///
    /// <paramref name="maxChars"/> satıra sığan karakter sayısı (EPL font 3/1x: 720/12 = 60).
    /// Taşarsa satır kâğıtta kırpılıyor — bu yüzden sığmadığında başlık kelimeleri
    /// ("Sevkiyat:", "Tarih:", "Parca:") düşer, veriler kalır.
    /// </summary>
    /// <summary>
    /// Künyenin ilk satırı: sevkiyat no + teslim tarihi.
    ///
    /// Parça ve desi buradan çıkarıldı — ikisi de artık sol alttaki çerçeveli kutuda
    /// ve orada çok daha okunur. Künye alanı 360–666 arasında yalnızca 306 dot;
    /// aynı bilgiyi iki kez yazınca satır kâğıttan taşıyordu (992 dot ölçüldü).
    ///
    /// <paramref name="maxChars"/> bir öneri değil sınırdır: uzun varyant sığmazsa
    /// kısası da kırpılır, çünkü kırpılmayan satır komşu alanın üstüne biner.
    /// </summary>
    private static string CargoKunyeLine(CargoLabelPayload p, int maxChars)
    {
        var full = $"Sevkiyat #{p.ShipmentId}  {p.DeliveryDate}";
        if (full.Length <= maxChars) return full;
        return Truncate($"#{p.ShipmentId}  {p.DeliveryDate}", maxChars);
    }

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
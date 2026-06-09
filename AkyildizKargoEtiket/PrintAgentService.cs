using Microsoft.Extensions.Options;
using System.Drawing.Printing;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AkyildizKargoEtiket;

// ── API DTOs ─────────────────────────────────────────────────────────────────

public record RegisterRequest(string AgentKey, string MachineName, string DisplayName, List<string> InstalledPrinters);
public record PendingJob(int Id, int LabelType, string PayloadJson, string PrinterName);
public record UpdateStatusRequest(int Status, string? ErrorMessage);
public record CargoLabelPayload(string Barcode, string ReceiverName, string Address, string Phone, string? IrsaliyeNo, int ShipmentId, string DeliveryDate);
public record BoxLabelPayload(string ProjectName, string Location, string ProjectCode, int BoxCount);

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

        _http.BaseAddress = new Uri(_opts.ServerUrl.TrimEnd('/') + "/");
        _http.Timeout     = TimeSpan.FromSeconds(10);
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
        string zpl = job.LabelType switch
        {
            0 => BuildCargoZpl(job),
            1 => BuildBoxZpl(job),
            _ => throw new InvalidOperationException($"Bilinmeyen etiket tipi: {job.LabelType}")
        };

        SendZplToPrinter(job.PrinterName, zpl);
        _log.LogInformation("Baskı tamamlandı: Job#{Id} → {Printer}", job.Id, job.PrinterName);
    }

    // ── ZPL Builders ─────────────────────────────────────────────────────────

    private static string BuildCargoZpl(PendingJob job)
    {
        var p = JsonSerializer.Deserialize<CargoLabelPayload>(job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Kargo payload parse edilemedi.");

        // Alıcı adını 25 karakterde kes — etiket taşmasın
        var name    = Truncate(p.ReceiverName, 28);
        var address = Truncate(p.Address,      40);
        var phone   = Truncate(p.Phone,        20);

        return $@"^XA
^CI31
^PW609
^LL406
^FO20,15^BY3^BCN,70,N,N,N^FD{p.Barcode}^FS
^FO20,95^A0N,18,18^FD{p.Barcode}^FS
^FO20,125^GB569,1,2^FS
^FO20,135^A0N,26,26^FD{name}^FS
^FO20,168^A0N,20,20^FD{address}^FS
^FO20,195^A0N,20,20^FDTel: {phone}^FS
^FO20,220^GB569,1,2^FS
^FO20,230^A0N,17,17^FDSevkiyat: #{p.ShipmentId}  Tarih: {p.DeliveryDate}^FS
^FO20,252^A0N,17,17^FD{(string.IsNullOrWhiteSpace(p.IrsaliyeNo) ? "" : "İrsaliye: " + p.IrsaliyeNo)}^FS
^FO20,278^GB569,1,2^FS
^FO20,288^A0N,20,20^FDGönderici: Akyıldız Lojistik A.Ş.^FS
^FO400,288^A0N,20,20^FDYURTIÇI KARGO^FS
^XZ";
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
^CI31
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

    private static void SendZplToPrinter(string printerName, string zpl)
    {
        // ZPL → Windows-1254 (Türkçe) — ^CI31 ile eşleşir
        var bytes = Encoding.GetEncoding(1254).GetBytes(zpl);

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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static int CenterX(string text, int labelWidth, int charWidth) =>
        Math.Max(0, (labelWidth - text.Length * charWidth) / 2);
}

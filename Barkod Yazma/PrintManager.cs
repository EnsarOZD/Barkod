using System.Runtime.InteropServices;
using System.Text;

public class PrintManager
{
    // Win32 API declarations for raw printing
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
    }

    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);

    [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static bool SendStringToPrinter(string printerName, string rawString)
    {
        // Windows-1254 (Türkçe ANSI) — EPL'de I8,A,001 codepage ile eşleşir
        var encoding = Encoding.GetEncoding(1254);
        var bytes = encoding.GetBytes(rawString);
        IntPtr pBytes = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, pBytes, bytes.Length);
        bool success = SendBytesToPrinter(printerName, pBytes, bytes.Length);
        Marshal.FreeCoTaskMem(pBytes);
        return success;
    }

    private static bool SendBytesToPrinter(string szPrinterName, IntPtr pBytes, int dwCount)
    {
        int dwError = 0, dwWritten = 0;
        IntPtr hPrinter = new IntPtr(0);
        DOCINFOA di = new DOCINFOA();
        bool bSuccess = false;
        string failStage = "";

        di.pDocName = "Depo Print Job";
        di.pDataType = "RAW";

        if (OpenPrinter(szPrinterName.Normalize(), out hPrinter, IntPtr.Zero))
        {
            if (StartDocPrinter(hPrinter, 1, di))
            {
                if (StartPagePrinter(hPrinter))
                {
                    bSuccess = WritePrinter(hPrinter, pBytes, dwCount, out dwWritten);
                    if (!bSuccess) { dwError = Marshal.GetLastWin32Error(); failStage = "WritePrinter"; }
                    EndPagePrinter(hPrinter);
                }
                else { dwError = Marshal.GetLastWin32Error(); failStage = "StartPagePrinter"; }
                
                EndDocPrinter(hPrinter);
            }
            else { dwError = Marshal.GetLastWin32Error(); failStage = "StartDocPrinter"; }
            
            ClosePrinter(hPrinter);
        }
        else { dwError = Marshal.GetLastWin32Error(); failStage = "OpenPrinter"; }

        if (!bSuccess)
        {
            // Fallback eğer error 0 gelirse (nadiren olur), genel metni koru.
            throw new Exception($"Yazıcıya gönderilemedi ({failStage}). Hata kodu: {dwError}");
        }
        return bSuccess;
    }

    /// <summary>
    /// Zebra LP 2844 için EPL komutlarını oluşturur ve yazıcıya gönderir (10x8cm Yatay, Ortalı).
    /// </summary>
    public static void PrintLabelEPL(string printerName, string projectName, string location, long projectCode, int boxCount)
    {
        string pName = projectName.ToUpper();
        string pLoc = location.ToUpper();
        string pCode = $"{projectCode} ({boxCount} KOLI)";

        // 10x8cm => Genişlik 800 dots, Yükseklik 640 dots (203 dpi)
        // Karakter sayısına göre dinamik Font ve Çarpan (M) hesaplayan yerel fonksiyon:
        (int font, int m, int charWidth) GetFontArgs(string txt)
        {
            // Eğer 8 karakterden azsa dev puntolar kullanılsın (Taşmayı önlemek için)
            if (txt.Length <= 8) return (5, 2, 48); 
            // 9-22 karakter arasıysa Orta-Büyük (Font 4 x2 - Fotoğraftaki 3. satır gibi çok net)
            if (txt.Length <= 22) return (4, 2, 28); 
            // Çok uzunsa standart boya düşsün
            return (4, 1, 14);                       
        }

        var f1 = GetFontArgs(pName);
        var f2 = GetFontArgs(pLoc);
        var f3 = GetFontArgs(pCode);

        // Ortalama matematiği: 800 dots varsayılan genişlik (Eğer gerçek kağıt daha darsa, bu pay taşırma yapabilir ancak dinamik kurallarımız bunu absorbe ediyor)
        int x1 = Math.Max(0, (800 - (pName.Length * f1.charWidth)) / 2);
        int x2 = Math.Max(0, (800 - (pLoc.Length * f2.charWidth)) / 2);
        int x3 = Math.Max(0, (800 - (pCode.Length * f3.charWidth)) / 2);

        string eplCommand =
$@"
I8,A,001
N
q800
Q640,24
A{x1},150,0,{f1.font},{f1.m},{f1.m},N,""{pName}""
A{x2},330,0,{f2.font},{f2.m},{f2.m},N,""{pLoc}""
A{x3},510,0,{f3.font},{f3.m},{f3.m},N,""{pCode}""
P{boxCount}
";
        SendStringToPrinter(printerName, eplCommand);
    }
}

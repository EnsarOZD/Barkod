namespace AkyildizKargoEtiket;

public class AgentOptions
{
    public string ServerUrl        { get; set; } = string.Empty;
    public string AgentKey         { get; set; } = string.Empty;
    public string DisplayName      { get; set; } = string.Empty;
    public int    PollIntervalSeconds { get; set; } = 3;

    /// <summary>
    /// Koli etiketindeki proje kodu rozeti dolu mu basılsın (varsayılan), yoksa
    /// 4 dot çerçeve + normal siyah metin mi? Direkt termalde geniş siyah alan leke
    /// yapıyorsa depo PC'sinde <c>false</c> yapılıp servis yeniden başlatılır —
    /// yerleşim iki varyantta birebir aynı, yeniden yayın gerekmez.
    /// </summary>
    public bool   KoliRozetDolgu     { get; set; } = true;
}

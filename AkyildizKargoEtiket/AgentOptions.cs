namespace AkyildizKargoEtiket;

public class AgentOptions
{
    public string ServerUrl        { get; set; } = string.Empty;
    public string AgentKey         { get; set; } = string.Empty;
    public string DisplayName      { get; set; } = string.Empty;
    public int    PollIntervalSeconds { get; set; } = 3;
}

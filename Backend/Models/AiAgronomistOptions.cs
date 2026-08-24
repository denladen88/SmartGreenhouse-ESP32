namespace SmartGreenhouse.Backend.Models;

public class AiAgronomistOptions
{
    public int PollIntervalMinutes { get; set; } = 60;
    public int TrendWindowMinutes { get; set; } = 360;
    public int TrendBucketMinutes { get; set; } = 5;
    public int DecisionHistoryCount { get; set; } = 5;
}

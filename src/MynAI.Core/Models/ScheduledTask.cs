using System.Text.Json.Serialization;

namespace MynAI.Core.Models;

public enum ScheduledAction
{
    Shutdown,
    Sleep,
    Lock,
    Restart}

public enum ScheduledTaskStatus
{
    Pending,
    Cancelled,
    Executing,
    Completed,
    Failed
}

public sealed class ScheduledTask
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public ScheduledAction Action { get; init; }

    public DateTimeOffset ExecuteAt { get; init; }

    public ScheduledTaskStatus Status { get; set; } = ScheduledTaskStatus.Pending;

    [JsonIgnore]
    public string DisplayStatus => Status switch
    {
        ScheduledTaskStatus.Pending => "Bekliyor",
        ScheduledTaskStatus.Cancelled => "İptal edildi",
        ScheduledTaskStatus.Executing => "Çalışıyor",
        ScheduledTaskStatus.Completed => "Tamamlandı",
        ScheduledTaskStatus.Failed => "Başarısız",
        _ => "Bilinmiyor"
    };

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string? Description { get; init; }
}

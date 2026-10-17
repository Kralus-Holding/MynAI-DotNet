using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using MynAI.Core.Models;

namespace MynAI.Agent.Notifications;

public sealed class AgentNotificationService
{
    private readonly bool _available;

    public AgentNotificationService()
    {
        try
        {
            AppNotificationManager.Default.Register();

            _available = true;

            Console.WriteLine(
                "Windows bildirim sistemi aktif.");
        }
        catch (Exception ex)
        {
            _available = false;

            Console.WriteLine(
                $"Windows bildirim sistemi başlatılamadı: {ex.Message}");
        }
    }

    public bool SendScheduledWarning(
        ScheduledTask task,
        TimeSpan remaining)
    {
        if (!_available)
            return false;

        try
        {
            var actionText =
                task.Action switch
                {
                    ScheduledAction.Shutdown =>
                        "kapatma",

                    ScheduledAction.Sleep =>
                        "uyku",

                    ScheduledAction.Lock =>
                        "kilitleme",

                    ScheduledAction.Restart =>
                        "yeniden başlatma",

                    _ =>
                        "işlem"
                };

            var remainingText =
                remaining.TotalMinutes >= 1
                    ? $"{Math.Max(
                        1,
                        (int)Math.Ceiling(
                            remaining.TotalMinutes))} dakika"
                    : "1 dakikadan kısa süre";

            var notification =
                new AppNotificationBuilder()
                    .AddText("MynAi")
                    .AddText(
                        $"{remainingText} sonra " +
                        $"{actionText} işlemi gerçekleştirilecek.")
                    .BuildNotification();

            AppNotificationManager.Default.Show(
                notification);

            Console.WriteLine(
                $"10 dakika uyarısı gönderildi: {task.Id}");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Bildirim gönderilemedi: {ex.Message}");

            return false;
        }
    }

    public bool SendFiveMinuteWarning(
        ScheduledTask task)
    {
        if (!_available)
            return false;

        try
        {
            var actionText =
                task.Action switch
                {
                    ScheduledAction.Shutdown =>
                        "kapatma",

                    ScheduledAction.Sleep =>
                        "uyku",

                    ScheduledAction.Lock =>
                        "kilitleme",

                    ScheduledAction.Restart =>
                        "yeniden başlatma",

                    _ =>
                        "işlem"
                };

            var notification =
                new AppNotificationBuilder()
                    .AddText("MynAi")
                    .AddText(
                        $"5 dakika sonra {actionText} işlemi gerçekleştirilecek.")
                    .BuildNotification();

            AppNotificationManager.Default.Show(
                notification);

            Console.WriteLine(
                $"5 dakika uyarısı gönderildi: {task.Id}");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"5 dakika bildirimi gönderilemedi: {ex.Message}");

            return false;
        }
    }
}


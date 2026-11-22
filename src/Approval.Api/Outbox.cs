namespace Approval.Api;

public sealed record Notification(string EventId, string RequestId, string Kind);
public interface INotificationSink
{
    Task SendAsync(Notification notification, CancellationToken cancellationToken);
}

// Deliberately no email, webhook or external side effect in this reference project.
public sealed class LoggingNotificationSink(ILogger<LoggingNotificationSink> logger) : INotificationSink
{
    public Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        logger.LogInformation("Notification event {EventId} of kind {Kind}", notification.EventId, notification.Kind);
        return Task.CompletedTask;
    }
}

public sealed class OutboxPump(ApprovalStore store, INotificationSink sink)
{
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        Notification? notification = null;
        var token = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using (var connection = store.Open())
        using (var claim = ApprovalStore.Command(connection, null, """
            UPDATE Outbox SET LeaseToken=$token, LeaseUntil=$until, Attempts=Attempts+1
            WHERE Id=(SELECT Id FROM Outbox WHERE Delivered=0 AND AvailableAt<=$now AND LeaseUntil<=$now ORDER BY CreatedAt,Id LIMIT 1)
            AND Delivered=0 AND LeaseUntil<=$now
            RETURNING Id,RequestId,Kind
            """, ("$token", token), ("$until", now + 60), ("$now", now)))
        using (var reader = claim.ExecuteReader())
        {
            if (reader.Read()) notification = new(reader.GetString(0), reader.GetString(1), reader.GetString(2));
        }
        if (notification is null) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await sink.SendAsync(notification, timeout.Token);
            using var connection = store.Open();
            using var acknowledge = ApprovalStore.Command(connection, null,
                "UPDATE Outbox SET Delivered=1,LeaseToken=NULL,LeaseUntil=0 WHERE Id=$id AND LeaseToken=$token",
                ("$id", notification.EventId), ("$token", token));
            acknowledge.ExecuteNonQuery();
        }
        catch
        {
            using var connection = store.Open();
            using var release = ApprovalStore.Command(connection, null,
                "UPDATE Outbox SET LeaseToken=NULL,LeaseUntil=0,AvailableAt=$retry WHERE Id=$id AND LeaseToken=$token",
                ("$retry", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5), ("$id", notification.EventId), ("$token", token));
            release.ExecuteNonQuery();
            throw;
        }
        return true;
    }
}

public sealed class OutboxWorker(OutboxPump pump, IConfiguration configuration, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration.GetValue<bool>("Outbox:Disabled")) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await pump.RunOnceAsync(stoppingToken)) await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch
            {
                // Avoid logging payloads or raw downstream exceptions. Production needs metrics/alerts.
                logger.LogWarning("Outbox attempt failed; delivery remains pending.");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}

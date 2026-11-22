using Approval.Api;

namespace Approval.Tests;

public sealed class OutboxTests
{
    private static void Execute(ApprovalStore store, string sql)
    {
        using var connection = store.Open(); using var command = connection.CreateCommand();
        command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static long Scalar(ApprovalStore store, string sql)
    {
        using var connection = store.Open(); using var command = connection.CreateCommand();
        command.CommandText = sql; return (long)command.ExecuteScalar()!;
    }
    private static string Seed(ApiFixture app)
    {
        var request = app.Store.Submit(new("maker", "demo"), "Enable feature flag");
        return app.Store.Decide(Guid.Parse(request.Id), new("checker", "demo"), new(1, "Approved"), "first").EventId;
    }
    private sealed class Sink(bool fail = false) : INotificationSink
    {
        public readonly List<string> EventIds = [];
        public Task SendAsync(Notification notification, CancellationToken cancellationToken)
        {
            lock (EventIds) EventIds.Add(notification.EventId);
            if (fail) throw new InvalidOperationException("simulated downstream failure");
            return Task.CompletedTask;
        }
    }
    [Fact]
    public async Task Successful_delivery_is_acknowledged_and_not_polled_again()
    {
        using var app = new ApiFixture(); var id = Seed(app); var sink = new Sink(); var pump = new OutboxPump(app.Store, sink);
        Assert.True(await pump.RunOnceAsync()); Assert.False(await pump.RunOnceAsync());
        Assert.Equal(new[] { id }, sink.EventIds); Assert.Equal(1, Scalar(app.Store, "SELECT Delivered FROM Outbox"));
    }
    [Fact]
    public async Task Failed_delivery_stays_pending_and_retries_with_same_event_id()
    {
        using var app = new ApiFixture(); var id = Seed(app); var failed = new Sink(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OutboxPump(app.Store, failed).RunOnceAsync());
        Assert.Equal(0, Scalar(app.Store, "SELECT Delivered FROM Outbox"));
        var success = new Sink(); var pump = new OutboxPump(app.Store, success);
        Assert.False(await pump.RunOnceAsync()); // Backoff, not a hot loop.
        Execute(app.Store, "UPDATE Outbox SET AvailableAt=0");
        Assert.True(await pump.RunOnceAsync()); Assert.Equal(new[] { id }, success.EventIds);
        Assert.Equal(2, Scalar(app.Store, "SELECT Attempts FROM Outbox"));
    }
    [Fact]
    public async Task Active_lease_excludes_other_workers_but_expired_lease_is_recoverable()
    {
        using var app = new ApiFixture(); var id = Seed(app); var sink = new Sink(); var pump = new OutboxPump(app.Store, sink);
        Execute(app.Store, "UPDATE Outbox SET LeaseToken='crashed-worker',LeaseUntil=4102444800");
        Assert.False(await pump.RunOnceAsync());
        Execute(app.Store, "UPDATE Outbox SET LeaseUntil=0");
        Assert.True(await pump.RunOnceAsync()); Assert.Equal(new[] { id }, sink.EventIds);
    }
    [Fact]
    public async Task Racing_pumps_do_not_claim_same_active_event()
    {
        using var app = new ApiFixture(); Seed(app); var sink = new Sink();
        var result = await Task.WhenAll(Task.Run(() => new OutboxPump(app.Store, sink).RunOnceAsync()), Task.Run(() => new OutboxPump(app.Store, sink).RunOnceAsync()));
        Assert.Single(result, x => x); Assert.Single(sink.EventIds);
    }
    private sealed class DeliveredThenCrashedSink : INotificationSink
    {
        public readonly List<string> Delivered = [];
        public Task SendAsync(Notification notification, CancellationToken cancellationToken)
        {
            Delivered.Add(notification.EventId); // Simulated external acceptance, before local acknowledgment.
            throw new InvalidOperationException("Process failed after delivery but before acknowledgment.");
        }
    }
    [Fact]
    public async Task Crash_after_external_delivery_can_redeliver_same_id_not_exactly_once()
    {
        using var app = new ApiFixture(); var id = Seed(app);
        var crashed = new DeliveredThenCrashedSink();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OutboxPump(app.Store, crashed).RunOnceAsync());
        Assert.Equal(new[] { id }, crashed.Delivered);
        Execute(app.Store, "UPDATE Outbox SET AvailableAt=0");
        var sink = new Sink(); await new OutboxPump(app.Store, sink).RunOnceAsync();
        Assert.Equal(new[] { id }, sink.EventIds);
    }
}

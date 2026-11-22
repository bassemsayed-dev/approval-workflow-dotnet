using System.Security.Claims;
using Microsoft.Data.Sqlite;

namespace Approval.Api;

public sealed record Actor(string Subject, string Organization)
{
    public static Actor From(ClaimsPrincipal principal) => new(
        principal.FindFirstValue("sub")!, principal.FindFirstValue("org")!);
}
public sealed record CreateRequest(string Title);
public sealed record DecisionInput(int ExpectedVersion, string Decision);
public sealed record ApprovalRequest(string Id, string Organization, string Maker, string Title, string State, int Version);
public sealed record DecisionResult(string RequestId, string State, int Version, string EventId);
public sealed class WorkflowException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

// Explicit SQL makes the version guard and transaction boundary visible for review.
// This is a SQLite reference adapter, not a drop-in adapter for SQL Server.
public sealed class ApprovalStore(string connectionString)
{
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Requests(
                Id TEXT PRIMARY KEY, Org TEXT NOT NULL, Maker TEXT NOT NULL,
                Title TEXT NOT NULL CHECK(length(Title) BETWEEN 1 AND 160),
                State TEXT NOT NULL CHECK(State IN ('Submitted','Approved','Rejected')),
                Version INTEGER NOT NULL CHECK(Version>0));
            CREATE TABLE IF NOT EXISTS Audit(
                Id TEXT PRIMARY KEY, RequestId TEXT NOT NULL REFERENCES Requests(Id),
                Actor TEXT NOT NULL, Action TEXT NOT NULL, Version INTEGER NOT NULL, OccurredAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Decisions(
                Org TEXT NOT NULL, Actor TEXT NOT NULL, Key TEXT NOT NULL,
                RequestId TEXT NOT NULL REFERENCES Requests(Id), ExpectedVersion INTEGER NOT NULL,
                State TEXT NOT NULL, ResultVersion INTEGER NOT NULL, EventId TEXT NOT NULL,
                PRIMARY KEY(Org, Actor, Key));
            CREATE TABLE IF NOT EXISTS Outbox(
                Id TEXT PRIMARY KEY, RequestId TEXT NOT NULL REFERENCES Requests(Id),
                Kind TEXT NOT NULL, CreatedAt TEXT NOT NULL, Delivered INTEGER NOT NULL DEFAULT 0,
                Attempts INTEGER NOT NULL DEFAULT 0, AvailableAt INTEGER NOT NULL DEFAULT 0,
                LeaseToken TEXT, LeaseUntil INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS IX_Outbox_Pending ON Outbox(Delivered,AvailableAt,LeaseUntil);
            """;
        command.ExecuteNonQuery();
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    public ApprovalRequest Submit(Actor actor, string title)
    {
        var id = Guid.NewGuid().ToString();
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using (var insert = Command(connection, tx,
            "INSERT INTO Requests VALUES($id,$org,$maker,$title,'Submitted',1)",
            ("$id", id), ("$org", actor.Organization), ("$maker", actor.Subject), ("$title", title))) insert.ExecuteNonQuery();
        AddAudit(connection, tx, id, actor.Subject, "Submitted", 1);
        tx.Commit();
        return new(id, actor.Organization, actor.Subject, title, "Submitted", 1);
    }

    public ApprovalRequest Get(Guid id, Actor actor)
    {
        using var connection = Open();
        return Read(connection, null, id.ToString(), actor.Organization);
    }

    private static ApprovalRequest Read(SqliteConnection connection, SqliteTransaction? transaction, string id, string org)
    {
        using var command = Command(connection, transaction, "SELECT Id,Org,Maker,Title,State,Version FROM Requests WHERE Id=$id AND Org=$org",
            ("$id", id), ("$org", org));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new WorkflowException(404, "Request not found.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5));
    }

    public DecisionResult Decide(Guid id, Actor actor, DecisionInput input, string key)
    {
        using var connection = Open();
        // A non-deferred transaction takes SQLite's write reservation before the read.
        using var tx = connection.BeginTransaction(deferred: false);
        using (var replay = Command(connection, tx,
            "SELECT RequestId,ExpectedVersion,State,ResultVersion,EventId FROM Decisions WHERE Org=$org AND Actor=$actor AND Key=$key",
            ("$org", actor.Organization), ("$actor", actor.Subject), ("$key", key)))
        using (var reader = replay.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != id.ToString() || reader.GetInt32(1) != input.ExpectedVersion || reader.GetString(2) != input.Decision)
                    throw new WorkflowException(409, "Idempotency key was already used with different input.");
                return new(reader.GetString(0), reader.GetString(2), reader.GetInt32(3), reader.GetString(4));
            }
        }
        var request = Read(connection, tx, id.ToString(), actor.Organization);
        if (request.Maker == actor.Subject) throw new WorkflowException(403, "A maker cannot decide their own request.");
        using var update = Command(connection, tx,
            "UPDATE Requests SET State=$state,Version=Version+1 WHERE Id=$id AND Org=$org AND State='Submitted' AND Version=$version",
            ("$state", input.Decision), ("$id", id.ToString()), ("$org", actor.Organization), ("$version", input.ExpectedVersion));
        if (update.ExecuteNonQuery() != 1) throw new WorkflowException(409, "Request state or version has changed.");
        var version = request.Version + 1;
        var eventId = Guid.NewGuid().ToString();
        AddAudit(connection, tx, request.Id, actor.Subject, input.Decision, version);
        using (var outbox = Command(connection, tx,
            "INSERT INTO Outbox(Id,RequestId,Kind,CreatedAt) VALUES($event,$id,$kind,$time)",
            ("$event", eventId), ("$id", request.Id), ("$kind", input.Decision), ("$time", DateTimeOffset.UtcNow.ToString("O")))) outbox.ExecuteNonQuery();
        using (var save = Command(connection, tx,
            "INSERT INTO Decisions VALUES($org,$actor,$key,$id,$expected,$state,$version,$event)",
            ("$org", actor.Organization), ("$actor", actor.Subject), ("$key", key), ("$id", request.Id),
            ("$expected", input.ExpectedVersion), ("$state", input.Decision), ("$version", version), ("$event", eventId))) save.ExecuteNonQuery();
        tx.Commit();
        return new(request.Id, input.Decision, version, eventId);
    }

    private static void AddAudit(SqliteConnection connection, SqliteTransaction tx, string id, string actor, string action, int version)
    {
        using var command = Command(connection, tx,
            "INSERT INTO Audit VALUES($audit,$id,$actor,$action,$version,$time)",
            ("$audit", Guid.NewGuid().ToString()), ("$id", id), ("$actor", actor), ("$action", action),
            ("$version", version), ("$time", DateTimeOffset.UtcNow.ToString("O")));
        command.ExecuteNonQuery();
    }
}

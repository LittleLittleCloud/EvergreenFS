using Microsoft.Data.Sqlite;

namespace EvergreenFS;

public sealed class SqliteFileSystem : VersionedFileSystem
{
    private readonly SqliteConnection connection;
    public SqliteFileSystem(string databasePath, string mount = "/v") : this(databasePath, mount, null, null) { }
    private SqliteFileSystem(string databasePath, string mount, Dictionary<string, byte[]?>? seed, Dictionary<string, FileAttributes>? attributes) : base(mount)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath) }.ToString());
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata (mount TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS seed_attributes (attributes BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS revisions (
                id INTEGER PRIMARY KEY, operation TEXT NOT NULL, path TEXT NOT NULL,
                timestamp TEXT NOT NULL, state BLOB NOT NULL);
            """;
        schema.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        using var mountCommand = connection.CreateCommand();
        mountCommand.Transaction = transaction;
        mountCommand.CommandText = "SELECT mount FROM metadata LIMIT 1";
        var storedMount = mountCommand.ExecuteScalar() as string;
        if (storedMount != null && storedMount != Mount)
        {
            connection.Close();
            throw new ArgumentException($"Database mount is '{storedMount}', not '{Mount}'.", nameof(mount));
        }
        if (storedMount == null)
        {
            mountCommand.CommandText = "INSERT INTO metadata VALUES ($mount)";
            mountCommand.Parameters.AddWithValue("$mount", Mount);
            mountCommand.ExecuteNonQuery();
        }
        var history = new List<(Revision, Dictionary<string, byte[]?>)>();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT id, operation, path, timestamp, state FROM revisions ORDER BY id";
        using (var reader = read.ExecuteReader())
            while (reader.Read())
                history.Add((new Revision(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture)),
                    Deserialize((byte[])reader[4])));
        var initial = history.Count == 0 ? seed ?? new() { ["/"] = null } : history[^1].Item2;
        using var attributeCommand = connection.CreateCommand();
        attributeCommand.Transaction = transaction;
        attributeCommand.CommandText = "SELECT attributes FROM seed_attributes LIMIT 1";
        var storedAttributes = attributeCommand.ExecuteScalar() as byte[];
        if (storedAttributes != null)
            attributes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, FileAttributes>>(storedAttributes);
        else
        {
            attributeCommand.CommandText = "INSERT INTO seed_attributes VALUES ($attributes)";
            attributeCommand.Parameters.AddWithValue("$attributes", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(attributes ?? new()));
            attributeCommand.ExecuteNonQuery();
        }
        Initialize(initial, history, attributes);
        if (history.Count == 0) Insert(History[0], initial, transaction);
        transaction.Commit();
    }
    private void Insert(Revision revision, Dictionary<string, byte[]?> state, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO revisions VALUES ($id, $operation, $path, $timestamp, $state)";
        command.Parameters.AddWithValue("$id", revision.Id);
        command.Parameters.AddWithValue("$operation", revision.Operation);
        command.Parameters.AddWithValue("$path", revision.Path);
        command.Parameters.AddWithValue("$timestamp", revision.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$state", Serialize(state));
        command.ExecuteNonQuery();
    }
    protected override void Persist(Revision revision, Dictionary<string, byte[]?> next)
    {
        using var transaction = connection.BeginTransaction();
        Insert(revision, next, transaction);
        transaction.Commit();
    }
    public void ExportFork(long revision, string newDatabasePath)
    {
        var snapshot = Snapshot(revision);
        using (new FileStream(newDatabasePath, FileMode.CreateNew, FileAccess.Write)) { }
        using var fork = new SqliteFileSystem(newDatabasePath, Mount, snapshot.CopyEntries(), snapshot.CopyAttributes());
    }
    protected override void DisposeCore() => connection.Dispose();
}

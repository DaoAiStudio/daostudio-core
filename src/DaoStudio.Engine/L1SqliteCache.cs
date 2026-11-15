using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaoStudio.Engine.Abstractions;
using Microsoft.Data.Sqlite;

namespace DaoStudio.Engine;

public sealed class L1SqliteCache : ITranslationCache, IDisposable, IAsyncDisposable
{
    // Process-local session key. Supply a stable private key to reopen the cache across processes.
    private static readonly byte[] SessionKey = RandomNumberGenerator.GetBytes(32);
    private readonly SqliteConnection connection;
    private readonly SqliteCommand read;
    private readonly SqliteCommand write;
    private readonly AesGcm cipher;
    private readonly object gate = new();
    private bool disposed;

    public L1SqliteCache(string databasePath, byte[]? encryptionKey = null)
    {
        var key = encryptionKey ?? SessionKey;
        if (key.Length != 32) throw new ArgumentException("AES key must contain 32 bytes.", nameof(encryptionKey));
        cipher = new AesGcm(key, 16);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Pooling = false, DefaultTimeout = 30 }.ToString());
        try
        {
            connection.Open();
            using var setup = connection.CreateCommand();
            setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; " +
                "CREATE TABLE IF NOT EXISTS l1_translations (game_id TEXT NOT NULL, target_lang TEXT NOT NULL, " +
                "template_hash TEXT NOT NULL, glossary_version INTEGER NOT NULL, payload BLOB NOT NULL, " +
                "PRIMARY KEY (game_id,target_lang,template_hash,glossary_version)) WITHOUT ROWID;";
            setup.ExecuteNonQuery();
            read = MakeCommand("SELECT payload FROM l1_translations WHERE game_id=$g AND target_lang=$l AND template_hash=$h AND glossary_version=$v");
            write = MakeCommand("INSERT INTO l1_translations VALUES ($g,$l,$h,$v,$p) ON CONFLICT(game_id,target_lang,template_hash,glossary_version) DO UPDATE SET payload=excluded.payload", true);
        }
        catch { connection.Dispose(); cipher.Dispose(); throw; }
    }
    private SqliteCommand MakeCommand(string sql, bool payload = false)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var name in new[] { "$g", "$l", "$h" }) command.Parameters.Add(name, SqliteType.Text);
        command.Parameters.Add("$v", SqliteType.Integer);
        if (payload) command.Parameters.Add("$p", SqliteType.Blob);
        command.Prepare();
        return command;
    }
    private static void Bind(SqliteCommand command, CacheKey key)
    {
        command.Parameters["$g"].Value = key.GameId;
        command.Parameters["$l"].Value = key.TargetLang;
        command.Parameters["$h"].Value = key.TemplateHash;
        command.Parameters["$v"].Value = key.GlossaryVersion;
    }
    private static byte[] AssociatedData(CacheKey key) => JsonSerializer.SerializeToUtf8Bytes(key);
    public ValueTask<CacheLookupResult> TryGetAsync(CacheKey key, CancellationToken ct = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ct.ThrowIfCancellationRequested();
            Bind(read, key);
            if (read.ExecuteScalar() is not byte[] payload) return ValueTask.FromResult(new CacheLookupResult(false, null, "L1"));
            if (payload.Length < 28) throw new CryptographicException("Invalid encrypted payload.");
            var plain = new byte[payload.Length - 28];
            cipher.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plain, AssociatedData(key));
            try { return ValueTask.FromResult(new CacheLookupResult(true, Encoding.UTF8.GetString(plain), "L1")); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
    public ValueTask SetAsync(CacheKey key, string translatedTemplate, CancellationToken ct = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ct.ThrowIfCancellationRequested();
            var plain = Encoding.UTF8.GetBytes(translatedTemplate);
            var payload = new byte[plain.Length + 28];
            RandomNumberGenerator.Fill(payload.AsSpan(0, 12));
            try { cipher.Encrypt(payload.AsSpan(0, 12), plain, payload.AsSpan(28), payload.AsSpan(12, 16), AssociatedData(key)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Bind(write, key);
            write.Parameters["$p"].Value = payload;
            write.ExecuteNonQuery();
            return ValueTask.CompletedTask;
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            read.Dispose(); write.Dispose(); connection.Dispose(); cipher.Dispose();
        }
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

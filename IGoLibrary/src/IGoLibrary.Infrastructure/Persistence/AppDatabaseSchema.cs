using Microsoft.Data.Sqlite;

namespace IGoLibrary.Infrastructure.Persistence;

internal static class AppDatabaseSchema
{
    public const int ApplicationId = 0x49474F45;
    public const int CurrentVersion = 1;

    public static async Task EnsureMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS AppMetadata (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);

        command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppMetadata WHERE Key = 'application-id';";
        var existing = await command.ExecuteScalarAsync(cancellationToken);
        if (existing is string value && !string.Equals(value, ApplicationId.ToString("X8"), StringComparison.Ordinal))
        {
            throw new InvalidDataException("数据库不是 IGoLibrary 数据库。");
        }

        command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO AppMetadata(Key, Value) VALUES ('application-id', $id), ('schema-version', $version);";
        command.Parameters.AddWithValue("$id", ApplicationId.ToString("X8"));
        command.Parameters.AddWithValue("$version", CurrentVersion.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

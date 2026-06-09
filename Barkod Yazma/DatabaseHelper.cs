using System.Data;
using Microsoft.Data.Sqlite;
using Dapper;

public class DatabaseHelper
{
    private readonly string _connectionString;

    public DatabaseHelper(string dbPath = "printerData.db")
    {
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        string createTableQuery = @"
            CREATE TABLE IF NOT EXISTS PrintHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                IdempotencyKey TEXT UNIQUE,
                ProjectName TEXT NOT NULL,
                Location TEXT NOT NULL,
                ProjectCode INTEGER NOT NULL,
                BoxCount INTEGER NOT NULL,
                Status TEXT NOT NULL,
                ErrorMessage TEXT NULL,
                ClientIp TEXT NOT NULL,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );";
            
        connection.Execute(createTableQuery);
    }

    public bool IsDuplicate(string idempotencyKey)
    {
        using var connection = new SqliteConnection(_connectionString);
        var count = connection.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM PrintHistory WHERE IdempotencyKey = @Key", 
            new { Key = idempotencyKey });
        return count > 0;
    }

    public async Task<long> LogPrintAsync(string idempotencyKey, string projectName, string location, long projectCode, int boxCount, string status, string errorMessage, string clientIp)
    {
        using var connection = new SqliteConnection(_connectionString);
        string insertQuery = @"
            INSERT INTO PrintHistory (IdempotencyKey, ProjectName, Location, ProjectCode, BoxCount, Status, ErrorMessage, ClientIp)
            VALUES (@IdempotencyKey, @ProjectName, @Location, @ProjectCode, @BoxCount, @Status, @ErrorMessage, @ClientIp);
            SELECT last_insert_rowid();";
            
        return await connection.ExecuteScalarAsync<long>(insertQuery, new 
        { 
            IdempotencyKey = idempotencyKey,
            ProjectName = projectName,
            Location = location,
            ProjectCode = projectCode,
            BoxCount = boxCount,
            Status = status,
            ErrorMessage = errorMessage,
            ClientIp = clientIp
        });
    }

    public async Task<IEnumerable<dynamic>> GetRecentLogsAsync(int limit = 100)
    {
        using var connection = new SqliteConnection(_connectionString);
        return await connection.QueryAsync(
            "SELECT * FROM PrintHistory ORDER BY CreatedAt DESC LIMIT @Limit", 
            new { Limit = limit });
    }
}

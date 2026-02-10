#!/usr/bin/env dotnet-script
#r "nuget: Microsoft.Data.Sqlite, 8.0.0"

using Microsoft.Data.Sqlite;
using System;
using System.Threading.Tasks;

// Example 1: Check tables in an in-memory database
await CheckInMemoryDatabase();

// Example 2: Check tables in a file database
await CheckFileDatabase("test.db");

async Task CheckInMemoryDatabase()
{
    Console.WriteLine("=== Checking In-Memory Database ===");
    using var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync();

    // Create a test table
    using var createCmd = connection.CreateCommand();
    createCmd.CommandText = @"
        CREATE TABLE test_table (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL
        )";
    await createCmd.ExecuteNonQueryAsync();

    // Query sqlite_master to see all tables
    await ListTables(connection);
}

async Task CheckFileDatabase(string dbPath)
{
    Console.WriteLine($"\n=== Checking File Database: {dbPath} ===");

    // Delete if exists
    if (File.Exists(dbPath))
    {
        File.Delete(dbPath);
    }

    using var connection = new SqliteConnection($"Data Source={dbPath}");
    await connection.OpenAsync();

    // Create a test table
    using var createCmd = connection.CreateCommand();
    createCmd.CommandText = @"
        CREATE TABLE wolverine_incoming (
            id TEXT PRIMARY KEY,
            status TEXT NOT NULL,
            owner_id INTEGER NOT NULL,
            execution_time TEXT,
            attempts INTEGER DEFAULT 0,
            body BLOB NOT NULL,
            message_type TEXT NOT NULL,
            received_at TEXT,
            keep_until TEXT,
            timestamp TEXT DEFAULT (datetime('now'))
        )";
    await createCmd.ExecuteNonQueryAsync();

    // Query sqlite_master to see all tables
    await ListTables(connection);

    Console.WriteLine($"\nDatabase file created at: {Path.GetFullPath(dbPath)}");
}

async Task ListTables(SqliteConnection connection)
{
    using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
        SELECT
            type,
            name,
            tbl_name,
            sql
        FROM sqlite_master
        WHERE type IN ('table', 'index')
        ORDER BY type, name";

    using var reader = await cmd.ExecuteReaderAsync();

    Console.WriteLine("\nDatabase Objects:");
    Console.WriteLine("================");

    while (await reader.ReadAsync())
    {
        var type = reader.GetString(0);
        var name = reader.GetString(1);
        var tblName = reader.GetString(2);
        var sql = reader.IsDBNull(3) ? "" : reader.GetString(3);

        Console.WriteLine($"\n{type.ToUpper()}: {name}");
        if (type == "table")
        {
            Console.WriteLine($"SQL: {sql}");
        }
    }
}

Console.WriteLine("\n=== Done ===");

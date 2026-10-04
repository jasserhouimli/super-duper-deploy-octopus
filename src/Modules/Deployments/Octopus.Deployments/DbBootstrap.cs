using Microsoft.EntityFrameworkCore;

namespace Octopus.Deployments;

/// <summary>
/// Interim schema upgrade until real EF Core migrations land (roadmap).
/// EnsureCreated covers fresh databases; the statements below converge
/// pre-existing SQLite files (new tables + added columns). Postgres will
/// need proper migrations instead of this.
/// </summary>
public static class DbBootstrap
{
    public static void EnsureUpgraded(OctopusDbContext db)
    {
        db.Database.EnsureCreated();

        if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) != true)
            return;

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "AppWebhooks" (
                "AppId" TEXT NOT NULL CONSTRAINT "PK_AppWebhooks" PRIMARY KEY,
                "Secret" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "WebhookEvents" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_WebhookEvents" PRIMARY KEY,
                "AppId" TEXT NOT NULL,
                "DeliveryId" TEXT NOT NULL,
                "EventType" TEXT NOT NULL,
                "Ref" TEXT NULL,
                "CommitSha" TEXT NULL,
                "PayloadHash" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "DeploymentId" TEXT NULL,
                "Error" TEXT NULL,
                "ReceivedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WebhookEvents_AppId_DeliveryId" ON "WebhookEvents" ("AppId", "DeliveryId");
            CREATE INDEX IF NOT EXISTS "IX_WebhookEvents_AppId_ReceivedAt" ON "WebhookEvents" ("AppId", "ReceivedAt");
            CREATE TABLE IF NOT EXISTS "AppEnvVars" (
                "AppId" TEXT NOT NULL,
                "Key" TEXT NOT NULL,
                "Value" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_AppEnvVars" PRIMARY KEY ("AppId", "Key")
            );
            CREATE INDEX IF NOT EXISTS "IX_AppEnvVars_AppId" ON "AppEnvVars" ("AppId");
            """);

        if (!ColumnExists(db, "Deployments", "ProjectPath"))
            db.Database.ExecuteSqlRaw("""ALTER TABLE "Deployments" ADD COLUMN "ProjectPath" TEXT NULL;""");
        if (!ColumnExists(db, "Apps", "MaxMemoryMb"))
            db.Database.ExecuteSqlRaw("""ALTER TABLE "Apps" ADD COLUMN "MaxMemoryMb" INTEGER NOT NULL DEFAULT 512;""");
        if (!ColumnExists(db, "Apps", "CpuMillicores"))
            db.Database.ExecuteSqlRaw("""ALTER TABLE "Apps" ADD COLUMN "CpuMillicores" INTEGER NOT NULL DEFAULT 1000;""");
    }

    private static bool ColumnExists(OctopusDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = false;
        try
        {
            if (conn.State != System.Data.ConnectionState.Open)
            {
                conn.Open();
                openedHere = true;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        finally
        {
            if (openedHere)
                conn.Close();
        }
    }
}

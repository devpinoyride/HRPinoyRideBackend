using System.Text.Json;
using Dapper;
using Npgsql;

namespace PinoyRideHrApi.Data;

/// <summary>
/// Server-side audit logging. Every successful insert/update writes an
/// audit_log row (actor_id, action, table_name, record_id, details jsonb)
/// on the same connection/transaction as the mutation itself.
/// </summary>
public class AuditService
{
    public async Task AddAsync(
        NpgsqlConnection con,
        NpgsqlTransaction? tx,
        Guid? actorId,
        string action,
        string tableName,
        string? recordId,
        object? details = null)
    {
        var json = details == null
            ? null
            : JsonSerializer.Serialize(details, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

        await con.ExecuteAsync(
            """
            insert into audit_log (actor_id, action, table_name, record_id, details)
            values (@ActorId, @Action, @TableName, @RecordId, @Details::jsonb)
            """,
            new { ActorId = actorId, Action = action, TableName = tableName, RecordId = recordId, Details = json },
            transaction: tx);
    }
}
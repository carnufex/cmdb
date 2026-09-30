using Cmdb.Database.Model;
using Microsoft.EntityFrameworkCore;

namespace Cmdb.Database.Voice;

/// <summary>
/// The people who may call the operations agent in the demo (ADR-0015, #134): synthetic, like the access scopes, and
/// synced in the same step. Their groups decide what a verified call sees, through the access scopes (#22).
/// </summary>
public static class VoiceCallerCatalog
{
    public static IReadOnlyList<VoiceCaller> Demo { get; } =
    [
        new() { EmployeeId = "1001", Name = "Kim Lindqvist", Phone = "+46700001001", Role = "technician", Groups = ["cmdb-full"] },
        new() { EmployeeId = "2002", Name = "Sam Nyberg", Phone = "+46700002002", Role = "contractor", Groups = ["cmdb-region-nord"] },
        new() { EmployeeId = "3003", Name = "Robin Ekdahl", Phone = "+46700003003", Role = "noc", Groups = ["cmdb-full"] },
    ];

    /// <summary>Inserts or updates the demo callers by employee id and removes the ones no longer listed.</summary>
    public static async Task SyncAsync(CmdbDbContext db, CancellationToken ct = default)
    {
        var existing = await db.VoiceCallers.ToDictionaryAsync(c => c.EmployeeId, StringComparer.Ordinal, ct);
        foreach (var caller in Demo)
        {
            if (!existing.Remove(caller.EmployeeId, out var row))
            {
                row = new VoiceCaller { EmployeeId = caller.EmployeeId, Name = caller.Name, Phone = caller.Phone, Role = caller.Role };
                db.VoiceCallers.Add(row);
            }
            row.Name = caller.Name;
            row.Phone = caller.Phone;
            row.Role = caller.Role;
            row.Groups = caller.Groups;
        }
        db.VoiceCallers.RemoveRange(existing.Values);
        await db.SaveChangesAsync(ct);
    }
}

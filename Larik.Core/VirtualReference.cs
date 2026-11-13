namespace Larik.Models;

/// <summary>
/// A user-defined ("custom") reference for a column with no declared foreign key. Some schemas
/// model lookup relationships without constraints; this lets the reference preview work the same
/// way it does for a real FK. Stored only in Larik's local database, scoped to a workspace and
/// database — never written to the server.
/// </summary>
public sealed class VirtualReferenceRecord
{
    public required string Id { get; init; }

    public required string SpaceId { get; init; }

    public required string DatabaseName { get; init; }

    public required string SourceSchema { get; init; }

    public required string SourceTable { get; init; }

    public required string SourceColumn { get; init; }

    public required string TargetSchema { get; init; }

    public required string TargetTable { get; init; }

    public required string TargetColumn { get; init; }
}

/// <summary>Local storage for user-defined references; the shell supplies the real store.</summary>
public interface IVirtualReferenceStore
{
    IReadOnlyList<VirtualReferenceRecord> GetVirtualReferences(string spaceId, string databaseName);

    bool SaveVirtualReference(VirtualReferenceRecord reference);

    bool DeleteVirtualReference(string id);
}

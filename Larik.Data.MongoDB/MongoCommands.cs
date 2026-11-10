using MongoDB.Bson;

namespace Larik.Data.MongoDB;

/// <summary>The result of translating one T-SQL statement into driver commands.</summary>
internal abstract record MongoCommand(string Original);

/// <summary>Reads a collection into an aggregation pipeline.</summary>
internal sealed record MSelect(
    string Collection,
    List<BsonDocument> Pipeline,
    IReadOnlyList<string>? Columns,
    string Original) : MongoCommand(Original);

/// <summary>Writes one or more documents into a collection.</summary>
internal sealed record MInsert(string Collection, List<BsonDocument> Documents, string Original) : MongoCommand(Original);

/// <summary>Sets fields on the documents matching a filter.</summary>
internal sealed record MUpdate(string Collection, BsonDocument Filter, BsonDocument Update, bool Many, string Original) : MongoCommand(Original);

/// <summary>Removes the documents matching a filter.</summary>
internal sealed record MDelete(string Collection, BsonDocument Filter, bool Many, string Original) : MongoCommand(Original);

/// <summary>Switches the connection's default database.</summary>
internal sealed record MUse(string Database, string Original) : MongoCommand(Original);

/// <summary>Raised when a statement cannot be translated; the message is user-facing.</summary>
internal sealed class TranslationException(string message) : Exception(message);
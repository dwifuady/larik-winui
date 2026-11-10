using System.Globalization;
using MongoDB.Bson;

namespace Larik.Data.MongoDB;

/// <summary>
/// Turns BSON values into grid-friendly text. MongoDB has no SQL column types, so the raw value is
/// what the user sees; the job here is only to stop leaking driver representations:
/// <list type="bullet">
/// <item>UUID binary becomes a hyphenated GUID, not <c>UuidStandard:0x…</c>;</item>
/// <item>ObjectId becomes its hex string;</item>
/// <item>arrays and sub-documents render as plain JSON with those friendly scalars, not
/// extended JSON (<c>$binary</c>/<c>$oid</c>/<c>$date</c>).</item>
/// </list>
/// </summary>
internal static class MongoValueFormatter
{
    public static string? Format(BsonValue value) => value.BsonType switch
    {
        BsonType.Null => null,
        BsonType.Boolean => value.AsBoolean ? "true" : "false",
        BsonType.String => value.AsString,
        BsonType.DateTime => FormatDate(value.AsBsonDateTime),
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.Binary => FormatBinary(value.AsBsonBinaryData),
        BsonType.Array or BsonType.Document => Friendly(value).ToJson(),
        _ => value.ToString(),
    };

    private static string FormatDate(BsonDateTime date) =>
        date.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff 'Z'", CultureInfo.InvariantCulture);

    private static string FormatBinary(BsonBinaryData binary)
    {
        var bytes = binary.Bytes;
        var guid = binary.SubType switch
        {
            // Subtype 4 stores the RFC 4122 byte order; subtype 3 is the legacy .NET little-endian order.
            BsonBinarySubType.UuidStandard when bytes.Length == 16 => new Guid(bytes.AsSpan(), bigEndian: true),
            BsonBinarySubType.UuidLegacy when bytes.Length == 16 => new Guid(bytes),
            _ => (Guid?)null,
        };

        return guid?.ToString() ?? "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// A copy with BSON scalars replaced by friendly text, so <c>ToJson</c> emits readable values
    /// for arrays and sub-documents instead of extended-JSON type wrappers.
    /// </summary>
    private static BsonValue Friendly(BsonValue value) => value.BsonType switch
    {
        BsonType.ObjectId => BsonString.Create(value.AsObjectId.ToString()),
        BsonType.Binary => BsonString.Create(FormatBinary(value.AsBsonBinaryData)),
        BsonType.DateTime => BsonString.Create(FormatDate(value.AsBsonDateTime)),
        BsonType.Array => new BsonArray(value.AsBsonArray.Select(Friendly)),
        BsonType.Document => FriendlyDocument(value.AsBsonDocument),
        _ => value,
    };

    private static BsonDocument FriendlyDocument(BsonDocument source)
    {
        var document = new BsonDocument();
        foreach (var element in source)
        {
            document[element.Name] = Friendly(element.Value);
        }

        return document;
    }
}
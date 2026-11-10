using Larik.Data.MongoDB;
using MongoDB.Bson;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// Grid-text formatting for BSON values. MongoDB has no column types, so a UUID binary and an
/// ObjectId must read as themselves, not as driver internals; arrays/sub-documents must not leak
/// extended JSON.
/// </summary>
public sealed class MongoValueFormatterTests
{
    private static readonly byte[] UuidBytes = Convert.FromHexString("5942e9f966f64242a8a0774fcfaffba1");

    [Fact]
    public void A_uuid_binary_renders_as_a_hyphenated_guid()
    {
        var value = new BsonBinaryData(UuidBytes, BsonBinarySubType.UuidStandard);

        Assert.Equal("5942e9f9-66f6-4242-a8a0-774fcfaffba1", MongoValueFormatter.Format(value));
    }

    [Fact]
    public void An_array_of_uuid_binary_renders_readable_json()
    {
        // The exact bytes from the reported case: ["$binary": "vnmQeQqIS/Cpqg9fX1XCnQ=="].
        var bytes = Convert.FromBase64String("vnmQeQqIS/Cpqg9fX1XCnQ==");
        var value = new BsonArray { new BsonBinaryData(bytes, BsonBinarySubType.UuidStandard) };

        var text = MongoValueFormatter.Format(value);

        Assert.Contains("be799079-0a88-4bf0-a9aa-0f5f5f55c29d", text);
        Assert.DoesNotContain("$binary", text);
        Assert.DoesNotContain("base64", text);
    }

    [Fact]
    public void An_object_id_renders_as_its_hex_string()
    {
        var id = ObjectId.GenerateNewId();
        Assert.Equal(id.ToString(), MongoValueFormatter.Format(new BsonObjectId(id)));
    }

    [Fact]
    public void A_nested_document_uses_friendly_scalars_not_extended_json()
    {
        var id = ObjectId.GenerateNewId();
        var document = new BsonDocument
        {
            ["uuid"] = new BsonBinaryData(UuidBytes, BsonBinarySubType.UuidStandard),
            ["owner"] = new BsonObjectId(id),
            ["name"] = "trip",
        };

        var text = MongoValueFormatter.Format(document);

        Assert.Contains("5942e9f9-66f6-4242-a8a0-774fcfaffba1", text);
        Assert.Contains(id.ToString(), text);
        Assert.DoesNotContain("$binary", text);
        Assert.DoesNotContain("$oid", text);
    }

    [Fact]
    public void Scalars_read_naturally()
    {
        Assert.Null(MongoValueFormatter.Format(BsonNull.Value));
        Assert.Equal("true", MongoValueFormatter.Format(BsonBoolean.True));
        Assert.Equal("5", MongoValueFormatter.Format(new BsonInt32(5)));
        Assert.Equal("sample", MongoValueFormatter.Format(new BsonString("sample")));
        Assert.Equal(
            "2024-01-02 03:04:05.000 Z",
            MongoValueFormatter.Format(new BsonDateTime(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc))));
    }
}
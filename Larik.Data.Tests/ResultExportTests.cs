using System.Text.Json;
using Larik.Data;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// The export body is the only part of "Export results" that can be wrong without a window, so it
/// is proven here: quoting, NULL vs empty, line endings, and JSON shape.
/// </summary>
public sealed class ResultExportTests
{
    private static QueryResult Result(params string?[][] rows) => new()
    {
        Columns =
        [
            new QueryColumn { Name = "name", DataType = "nvarchar" },
            new QueryColumn { Name = "amount", DataType = "decimal" },
        ],
        Rows = rows,
        Message = "2 rows",
    };

    [Fact]
    public void Csv_writes_a_header_and_crlf_rows()
    {
        var csv = ResultExport.ToCsv(Result(
            ["Ada", "1.25"],
            ["Grace", "2"]));

        Assert.Equal("name,amount\r\nAda,1.25\r\nGrace,2\r\n", csv);
    }

    [Fact]
    public void Csv_quotes_commas_quotes_and_newlines()
    {
        var csv = ResultExport.ToCsv(Result(
            ["Hopper, Grace", "say \"hi\""],
            ["two\r\nlines", null]));

        Assert.Equal(
            "name,amount\r\n\"Hopper, Grace\",\"say \"\"hi\"\"\"\r\n\"two\r\nlines\",\r\n",
            csv);
    }

    [Fact]
    public void Csv_keeps_null_and_empty_string_distinguishable()
    {
        var csv = ResultExport.ToCsv(Result([null, ""]));

        // NULL is an empty field; an empty string is quoted.
        Assert.Equal("name,amount\r\n,\"\"\r\n", csv);
    }

    [Fact]
    public void Csv_quotes_leading_and_trailing_spaces_so_they_survive_a_reader_trim()
    {
        var csv = ResultExport.ToCsv(Result(["  padded  ", "1"]));
        Assert.Contains("\"  padded  \"", csv, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=WEBSERVICE(A1)")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\t=cmd")]
    [InlineData("\r=cmd")]
    public void Csv_neutralises_values_a_spreadsheet_would_run_as_formulas(string value)
    {
        var csv = ResultExport.ToCsv(Result([value, "1"]));

        // The apostrophe is Excel's text marker; the original text follows untouched.
        Assert.Contains("'" + value, csv, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n" + value, csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_keeps_a_formula_like_value_unchanged()
    {
        const string value = "=1+1";
        using var document = JsonDocument.Parse(ResultExport.ToJson(Result([value, "1"])));
        Assert.Equal(value, document.RootElement[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Json_is_an_array_of_objects_with_nulls()
    {
        var json = ResultExport.ToJson(Result(
            ["Ada", "1.25"],
            [null, ""]));

        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement;
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("Ada", rows[0].GetProperty("name").GetString());
        Assert.Equal("1.25", rows[0].GetProperty("amount").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("name").ValueKind);
        Assert.Equal(string.Empty, rows[1].GetProperty("amount").GetString());
    }

    [Fact]
    public void Json_suffixes_duplicate_column_names_instead_of_losing_a_value()
    {
        var result = new QueryResult
        {
            Columns =
            [
                new QueryColumn { Name = "id", DataType = "int" },
                new QueryColumn { Name = "id", DataType = "int" },
            ],
            Rows = [["1", "2"]],
            Message = "1 row",
        };

        using var document = JsonDocument.Parse(ResultExport.ToJson(result));
        var row = document.RootElement[0];
        Assert.Equal("1", row.GetProperty("id").GetString());
        Assert.Equal("2", row.GetProperty("id_2").GetString());
    }

    [Fact]
    public void Json_keeps_non_ascii_readable()
    {
        var json = ResultExport.ToJson(Result(["Zoë", "1"]));
        Assert.Contains("Zoë", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u00EB", json, StringComparison.OrdinalIgnoreCase);
    }
}

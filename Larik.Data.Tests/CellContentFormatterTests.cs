using Larik.Data;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// The cell preview's formatting rules. Pure string work, so it is proven here instead of through
/// the window: the panel only decides when to call <see cref="CellContentFormatter"/>.
/// </summary>
public sealed class CellContentFormatterTests
{
    [Fact]
    public void Auto_indents_json_objects()
    {
        var result = CellContentFormatter.Format("""{"a":1,"b":[2,3]}""", CellFormat.Auto);

        Assert.Equal(CellFormat.Json, result.Kind);
        Assert.Null(result.Error);
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    2,\n    3\n  ]\n}", result.Text);
    }

    [Fact]
    public void Auto_indents_json_arrays()
    {
        var result = CellContentFormatter.Format("[1,2]", CellFormat.Auto);

        Assert.Equal(CellFormat.Json, result.Kind);
        Assert.Equal("[\n  1,\n  2\n]", result.Text);
    }

    [Fact]
    public void Auto_keeps_unicode_readable()
    {
        var result = CellContentFormatter.Format("""{"name":"Zoë"}""", CellFormat.Auto);

        Assert.Equal(CellFormat.Json, result.Kind);
        Assert.Contains("Zoë", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u00EB", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Auto_indents_xml_including_a_declaration()
    {
        var result = CellContentFormatter.Format(
            """<?xml version="1.0"?><root><a>1</a><b>2</b></root>""",
            CellFormat.Auto);

        Assert.Equal(CellFormat.Xml, result.Kind);
        Assert.Null(result.Error);
        Assert.Equal(
            "<?xml version=\"1.0\"?>\n<root>\n  <a>1</a>\n  <b>2</b>\n</root>",
            result.Text);
    }

    [Fact]
    public void Auto_leaves_plain_text_and_text_without_closing_tags_alone()
    {
        Assert.Equal(CellFormat.Plain, CellContentFormatter.Format("Ada,1.25", CellFormat.Auto).Kind);
        Assert.Equal(CellFormat.Plain, CellContentFormatter.Format("<br/>", CellFormat.Auto).Kind);
        Assert.Equal("{\"a\":1", CellContentFormatter.Format("{\"a\":1", CellFormat.Auto).Text);
    }

    [Fact]
    public void Explicit_json_on_invalid_text_keeps_the_raw_value_and_reports_it()
    {
        var result = CellContentFormatter.Format("not json", CellFormat.Json);

        Assert.Equal(CellFormat.Json, result.Kind);
        Assert.Equal("not json", result.Text);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Explicit_xml_on_non_xml_keeps_the_raw_value_and_reports_it()
    {
        var result = CellContentFormatter.Format("hello", CellFormat.Xml);

        Assert.Equal(CellFormat.Xml, result.Kind);
        Assert.Equal("hello", result.Text);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Plain_never_rewrites_the_value()
    {
        const string value = "{\"a\":1,\"b\":2}";
        var result = CellContentFormatter.Format(value, CellFormat.Plain);

        Assert.Equal(CellFormat.Plain, result.Kind);
        Assert.Equal(value, result.Text);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Oversized_values_stay_raw()
    {
        var value = "{" + new string('x', CellContentFormatter.MaxFormatLength) + "}";

        var auto = CellContentFormatter.Format(value, CellFormat.Auto);
        var explicitFormat = CellContentFormatter.Format(value, CellFormat.Json);

        Assert.Equal(value, auto.Text);
        Assert.Null(auto.Error);
        Assert.NotNull(explicitFormat.Error);
    }
}

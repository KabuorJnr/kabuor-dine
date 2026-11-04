// Report files people open in Excel, Google Sheets or LibreOffice. A small .xlsx writer (only the Office Open XML
// parts a workbook needs), so a report downloads as a real spreadsheet: money as numbers an accountant can add up,
// several sheets in one file, the column names frozen at the top. The same tables also write as CSV.
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Kabuor.Erp.Common;

/// <summary>Money in a report cell: kept as cents, written as shillings with two decimals.</summary>
public readonly record struct Ksh(long Cents);

/// <summary>A share in a report cell, as a percentage (25.5 means 25.5%).</summary>
public readonly record struct Pct(decimal Value);

public enum RowStyle { Plain, Section, Total }

public sealed record SheetRow(object?[] Cells, RowStyle Style = RowStyle.Plain);

/// <summary>One table: a sheet in a workbook, a CSV file, or a table on the printable report.</summary>
public sealed class Sheet(string name, string title, string subtitle, params string[] columns)
{
    public string Name { get; } = name;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string[] Columns { get; } = columns;
    public List<SheetRow> Rows { get; } = [];
    public string? Note { get; set; }

    public Sheet Add(params object?[] cells) { Rows.Add(new(cells)); return this; }
    public Sheet Section(string label) { Rows.Add(new([label], RowStyle.Section)); return this; }
    public Sheet Total(params object?[] cells) { Rows.Add(new(cells, RowStyle.Total)); return this; }
}

public static class Xlsx
{
    // Cell styles, by index into cellXfs below.
    const int Header = 1, Money = 2, MoneyBold = 3, Title = 4, Whole = 5, Note = 6, Bold = 7, Date = 8, Percent = 9, WholeBold = 10, PercentBold = 11, Muted = 12;

    public static byte[] Write(IReadOnlyList<Sheet> sheets)
    {
        if (sheets.Count == 0) throw new ArgumentException("A workbook needs at least one sheet.");
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var names = UniqueNames(sheets);
            Part(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
            Part(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            var wb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
            var rels = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
            for (var i = 0; i < sheets.Count; i++)
            {
                wb.Append($"""<sheet name="{Esc(names[i])}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
                rels.Append($"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>""");
                Part(zip, $"xl/worksheets/sheet{i + 1}.xml", Worksheet(sheets[i]));
            }
            rels.Append($"""<Relationship Id="rId{sheets.Count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Part(zip, "xl/workbook.xml", wb.Append("</sheets></workbook>").ToString());
            Part(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
            Part(zip, "xl/styles.xml", Styles);
        }
        return ms.ToArray();
    }

    static void Part(ZipArchive zip, string path, string xml)
    {
        using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(xml);
    }

    static string ContentTypes(int sheets)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (var i = 1; i <= sheets; i++) sb.Append($"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        return sb.Append("</Types>").ToString();
    }

    // Fonts: 0 normal, 1 bold, 2 title, 3 note (grey italic), 4 grey. Fills: 0 none, 1 required gray125, 2 header tint.
    // Number formats: 164 money, 165 whole numbers, 166 percent with one decimal, 14 (built in) dates.
    const string Styles = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="3"><numFmt numFmtId="164" formatCode="#,##0.00"/><numFmt numFmtId="165" formatCode="#,##0"/><numFmt numFmtId="166" formatCode="0.0&quot;%&quot;"/></numFmts><fonts count="5"><font><sz val="11"/><name val="Calibri"/><family val="2"/></font><font><b/><sz val="11"/><name val="Calibri"/><family val="2"/></font><font><b/><sz val="14"/><name val="Calibri"/><family val="2"/></font><font><i/><sz val="10"/><color rgb="FF585C80"/><name val="Calibri"/><family val="2"/></font><font><sz val="10"/><color rgb="FF585C80"/><name val="Calibri"/><family val="2"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEEF0F7"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="3"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style="thin"><color rgb="FFB8BBD0"/></bottom><diagonal/></border><border><left/><right/><top style="thin"><color rgb="FF585C80"/></top><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="13"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="164" fontId="1" fillId="0" borderId="2" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1"/><xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="0" fontId="3" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="14" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="166" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="1" fillId="0" borderId="2" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1"/><xf numFmtId="166" fontId="1" fillId="0" borderId="2" xfId="0" applyNumberFormat="1" applyFont="1" applyBorder="1"/><xf numFmtId="0" fontId="4" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles><dxfs count="0"/><tableStyles count="0"/></styleSheet>""";

    static string Worksheet(Sheet s)
    {
        var width = Enumerable.Range(0, Math.Max(1, s.Columns.Length)).Select(i => Math.Max(10, (s.Columns.ElementAtOrDefault(i)?.Length ?? 0) + 2)).ToArray();
        void Measure(object?[] cells)
        {
            for (var i = 0; i < cells.Length && i < width.Length; i++)
                width[i] = Math.Min(60, Math.Max(width[i], Display(cells[i]).Length + 2));
        }
        foreach (var r in s.Rows) if (r.Style != RowStyle.Section) Measure(r.Cells);

        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        // Title, what it covers, a gap, then the column names: those stay in view while scrolling.
        sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="4" topLeftCell="A5" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><cols>""");
        for (var i = 0; i < width.Length; i++) sb.Append($"""<col min="{i + 1}" max="{i + 1}" width="{width[i]}" customWidth="1"/>""");
        sb.Append("</cols><sheetData>");
        var row = 0;
        void Row(IEnumerable<(object? Value, int Style)> cells)
        {
            row++;
            sb.Append($"""<row r="{row}">""");
            var col = 0;
            foreach (var (value, style) in cells) Cell(sb, $"{Column(col++)}{row}", value, style);
            sb.Append("</row>");
        }
        Row([(s.Title, Title)]);
        Row([(s.Subtitle, Muted)]);
        Row([]);
        Row(s.Columns.Select(c => ((object?)c, Header)));
        foreach (var r in s.Rows)
            Row(r.Style == RowStyle.Section
                ? [(r.Cells.FirstOrDefault(), Bold)]
                : r.Cells.Select((c, i) => (c, StyleOf(c, r.Style == RowStyle.Total, i == 0))));
        if (!string.IsNullOrWhiteSpace(s.Note)) { Row([]); Row([(s.Note, Note)]); }
        return sb.Append("</sheetData></worksheet>").ToString();
    }

    static int StyleOf(object? value, bool total, bool first) => value switch
    {
        Ksh => total ? MoneyBold : Money,
        Pct => total ? PercentBold : Percent,
        int or long => total ? WholeBold : Whole,
        decimal or double => total ? WholeBold : 0,
        DateOnly => Date,
        _ => total && first ? Bold : 0,
    };

    static void Cell(StringBuilder sb, string reference, object? value, int style)
    {
        var s = style == 0 ? "" : $""" s="{style}" """.TrimEnd();
        switch (value)
        {
            case null: return;
            case Ksh k: sb.Append($"""<c r="{reference}"{s}><v>{(k.Cents / 100m).ToString(CultureInfo.InvariantCulture)}</v></c>"""); return;
            case Pct p: sb.Append($"""<c r="{reference}"{s}><v>{p.Value.ToString(CultureInfo.InvariantCulture)}</v></c>"""); return;
            case int or long or decimal or double: sb.Append($"""<c r="{reference}"{s}><v>{Convert.ToString(value, CultureInfo.InvariantCulture)}</v></c>"""); return;
            case DateOnly d: sb.Append($"""<c r="{reference}"{s}><v>{d.DayNumber - new DateOnly(1899, 12, 30).DayNumber}</v></c>"""); return;
            default:
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                if (text.Length == 0) return;
                sb.Append($"""<c r="{reference}" t="inlineStr"{s}><is><t xml:space="preserve">{Esc(text)}</t></is></c>""");
                return;
        }
    }

    /// <summary>How a cell reads, for sizing columns and for CSV.</summary>
    public static string Display(object? value) => value switch
    {
        null => "",
        Ksh k => (k.Cents / 100m).ToString("#,##0.00", CultureInfo.InvariantCulture),
        Pct p => $"{p.Value.ToString("0.0", CultureInfo.InvariantCulture)}%",
        DateOnly d => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    static string Column(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    static string Esc(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch < 0x20 && ch is not ('\t' or '\n' or '\r')) continue; // not allowed in XML
            sb.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => ch.ToString() });
        }
        return sb.ToString();
    }

    /// <summary>Sheet names: at most 31 characters, none of []:*?/\, and no two alike.</summary>
    static List<string> UniqueNames(IReadOnlyList<Sheet> sheets)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return sheets.Select(s =>
        {
            var name = new string(s.Name.Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim();
            if (name.Length == 0) name = "Sheet";
            name = name[..Math.Min(name.Length, 31)];
            var candidate = name;
            for (var n = 2; !used.Add(candidate); n++) candidate = $"{name[..Math.Min(name.Length, 28)]} {n}";
            return candidate;
        }).ToList();
    }

    /// <summary>The same table as CSV for any program: column names, then rows; money as plain numbers.</summary>
    public static string Csv(Sheet s)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', s.Columns.Select(CsvCell.Of)));
        foreach (var r in s.Rows)
            sb.AppendLine(string.Join(',', r.Cells.Select(c => CsvCell.Of(c switch
            {
                Ksh k => (k.Cents / 100m).ToString("0.00", CultureInfo.InvariantCulture),
                Pct p => p.Value.ToString("0.0", CultureInfo.InvariantCulture),
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => Convert.ToString(c, CultureInfo.InvariantCulture),
            }))));
        return sb.ToString();
    }
}

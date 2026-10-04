using ClosedXML.Excel;

namespace Omnichannel.Infrastructure.Excel;

/// <summary>A column of a data sheet: its header, its width and - for numbers, dates and times - its number format.</summary>
internal sealed record ReportColumn(string Header, double MinWidth, string? Format = null);

/// <summary>
/// One data sheet of an export: a bold, frozen header row with an auto-filter, then one row per record, written as it
/// arrives. Column widths are fixed (from the header, never measured over the data) and every cell is typed:
/// <list type="bullet">
/// <item><b>text is always a value, never a formula</b> - a cell gets its content through <c>Value</c>, which stores a
/// string as text whatever it starts with (<c>=1+1</c>, <c>@SUM(A1)</c>); a formula can only be set through
/// <c>FormulaA1</c>, which this class never uses;</item>
/// <item>numbers are numbers, money with <see cref="MoneyFormat"/>;</item>
/// <item>a point in time is an Excel date-time in the time zone of the export (Excel knows no time zone: the zone is in
/// the header); a date is an Excel date.</item>
/// </list>
/// </summary>
internal sealed class ReportSheet
{
    public const string MoneyFormat = "#,##0";
    public const string DateTimeFormat = "yyyy-mm-dd hh:mm:ss";
    public const string DateFormat = "yyyy-mm-dd";

    /// <summary>Room for the filter button next to the header text.</summary>
    private const int HeaderPadding = 4;

    private const int HeaderRow = 1;

    private const char Apostrophe = '\'';

    private readonly IXLWorksheet _sheet;
    private readonly int _columns;
    private readonly TimeZoneInfo _timeZone;
    private int _row = HeaderRow;

    public ReportSheet(XLWorkbook workbook, string name, IReadOnlyList<ReportColumn> columns, TimeZoneInfo timeZone)
    {
        _sheet = workbook.AddWorksheet(name);
        _columns = columns.Count;
        _timeZone = timeZone;

        for (var index = 0; index < columns.Count; index++)
        {
            var column = _sheet.Column(index + 1);
            column.Width = Math.Max(columns[index].MinWidth, columns[index].Header.Length + HeaderPadding);
            if (columns[index].Format is { } format)
            {
                // Set before any cell of the column exists: every cell written later takes it over.
                column.Style.NumberFormat.Format = format;
            }

            _sheet.Cell(HeaderRow, index + 1).Value = columns[index].Header;
        }

        _sheet.Range(HeaderRow, 1, HeaderRow, _columns).Style.Font.Bold = true;
        _sheet.SheetView.FreezeRows(HeaderRow);
    }

    public int DataRows => _row - HeaderRow;

    public void BeginRow() => _row++;

    public void Text(int column, string? value) => SetText(_sheet.Cell(_row, column), value);

    public void Number(int column, int value) => _sheet.Cell(_row, column).Value = value;

    public void Number(int column, decimal value) => _sheet.Cell(_row, column).Value = value;

    /// <summary>The local date and time of <paramref name="value"/> in the export's time zone.</summary>
    public void Moment(int column, DateTimeOffset value) =>
        _sheet.Cell(_row, column).Value = LocalTimeOf(value, _timeZone);

    public void Date(int column, DateOnly? value)
    {
        if (value is { } date)
        {
            _sheet.Cell(_row, column).Value = date.ToDateTime(TimeOnly.MinValue);
        }
    }

    /// <summary>The auto-filter over the header and every data row. Call once, after the last row.</summary>
    public void Finish() => _sheet.Range(HeaderRow, 1, _row, _columns).SetAutoFilter();

    /// <summary>
    /// Writes <paramref name="value"/> as the text of the cell, exactly as it is; an empty value leaves the cell empty.
    /// The library reads a leading apostrophe the way Excel does when it is typed - as the mark "this is text" - and
    /// drops it; written twice, one stays as content, so a name that starts with an apostrophe keeps it.
    /// </summary>
    public static void SetText(IXLCell cell, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            cell.Value = value.StartsWith(Apostrophe) ? Apostrophe + value : value;
        }
    }

    /// <summary>What a clock in <paramref name="timeZone"/> shows at <paramref name="value"/> (daylight saving time included).</summary>
    public static DateTime LocalTimeOf(DateTimeOffset value, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(value, timeZone).DateTime;
}

/// <summary>The <i>Info</i> sheet of an export: one labelled value per row (what was asked for, when, by whom, how much).</summary>
internal sealed class InfoSheet
{
    private const double LabelWidth = 34;
    private const double ValueWidth = 48;

    private readonly IXLWorksheet _sheet;
    private readonly TimeZoneInfo _timeZone;
    private int _row;

    public InfoSheet(XLWorkbook workbook, string name, TimeZoneInfo timeZone)
    {
        _sheet = workbook.AddWorksheet(name);
        _timeZone = timeZone;
        _sheet.Column(1).Width = LabelWidth;
        _sheet.Column(1).Style.Font.Bold = true;
        _sheet.Column(2).Width = ValueWidth;
        _sheet.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
    }

    /// <summary>A text value (as a value, never a formula); an empty one leaves the cell empty.</summary>
    public void Text(string label, string? value) => ReportSheet.SetText(NextValueCell(label), value);

    public void Number(string label, int value) => NextValueCell(label).Value = value;

    public void Moment(string label, DateTimeOffset value)
    {
        var cell = NextValueCell(label);
        cell.Style.NumberFormat.Format = ReportSheet.DateTimeFormat;
        cell.Value = ReportSheet.LocalTimeOf(value, _timeZone);
    }

    private IXLCell NextValueCell(string label)
    {
        _row++;
        _sheet.Cell(_row, 1).Value = label;
        return _sheet.Cell(_row, 2);
    }
}

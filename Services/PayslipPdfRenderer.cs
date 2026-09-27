using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PinoyRideHrApi.Services;

/// <summary>
/// Renders the bulk payroll summary as a printable payslip report (PDF).
///
/// The caller decides which columns survive the "all zero / blank" filter and
/// supplies the already-formatted cell text, so this class only handles layout
/// and styling — the payroll numbers are rendered verbatim. Landscape A4 keeps
/// the wide numeric breakdown readable, and QuestPDF paginates automatically
/// (repeating the header row) when there are many employees.
/// </summary>
public static class PayslipPdfRenderer
{
    // Palette mirrors the earlier HTML report so both formats look alike.
    private const string Ink = "#1f2933";
    private const string NameInk = "#14396b";
    private const string Border = "#1f3a5f";
    private const string GridLine = "#9fb3c8";
    private const string TitleFill = "#e8eef7";
    private const string HeaderFill = "#dce6f2";
    private const string ZebraFill = "#eef7ee";

    /// <summary>
    /// Builds the PDF bytes. <paramref name="title"/> is the bordered title bar,
    /// <paramref name="headers"/> the column labels, <paramref name="numericFlags"/>
    /// marks the right-aligned columns, and <paramref name="totalRow"/> is the bold
    /// summary row rendered under the table.
    /// </summary>
    public static byte[] Render(
        string title,
        IReadOnlyList<string> headers,
        IReadOnlyList<bool> numericFlags,
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<string> totalRow)
    {
        // QuestPDF requires an explicit licence type. Community is free for
        // open-source use and for organisations below the revenue threshold;
        // switch to LicenseType.Commercial with a purchased key beyond that.
        QuestPDF.Settings.License = LicenseType.Community;

        return Document.Create(c =>
        {
            c.Page(p =>
            {
                p.Size(PageSizes.A4.Landscape());
                p.Margin(14, Unit.Millimetre);
                p.DefaultTextStyle(t => t.FontSize(7).FontFamily("Lato").FontColor(Ink));

                p.Header().Element(e => TitleBar(e, title));
                p.Content().Element(e => Table(e, headers, numericFlags, rows, totalRow));
            });
        }).GeneratePdf();
    }

    private static void TitleBar(IContainer container, string title)
    {
        // Bordered, centered, bold title bar showing the cutoff period.
        container.Border(2).BorderColor(Border)
            .Background(TitleFill)
            .Padding(8, Unit.Millimetre)
            .AlignCenter()
            .Text(title)
            .SemiBold().FontSize(13).FontColor(Border);
    }

    private static void Table(
        IContainer container,
        IReadOnlyList<string> headers,
        IReadOnlyList<bool> numericFlags,
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<string> totalRow)
    {
        container.PaddingTop(8).Table(table =>
        {
            // The employee name needs extra room; numeric columns are narrower.
            table.ColumnsDefinition(cd =>
            {
                for (var i = 0; i < headers.Count; i++)
                {
                    // RelativeColumn's argument is the column's width ratio.
                    cd.RelativeColumn(numericFlags[i] ? 0.6f : 1f);
                }
            });

            // Bold shaded header row; QuestPDF repeats it on every page.
            table.Header(header =>
            {
                for (var i = 0; i < headers.Count; i++)
                {
                    var numeric = numericFlags[i];
                    var label = headers[i];
                    header.Cell().Element(c => c
                        .Background(HeaderFill)
                        .BorderBottom(2).BorderColor(Border)
                        .Padding(3, Unit.Point)
                        .AlignTo(numeric)
                        .Text(label)
                        .SemiBold().FontColor(Border));
                }
            });

            // Body rows with alternating shading.
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var shaded = rowIndex % 2 == 1;
                for (var colIndex = 0; colIndex < headers.Count; colIndex++)
                {
                    var numeric = colIndex < numericFlags.Count && numericFlags[colIndex];
                    var isName = colIndex == 0;
                    var text = colIndex < row.Count ? row[colIndex] ?? "" : "";
                    table.Cell().Element(c => c
                        .Border(0.5f).BorderColor(GridLine)
                        .Background(shaded ? ZebraFill : Colors.White)
                        .Padding(3, Unit.Point)
                        .AlignTo(numeric)
                        .Text(text)
                        .FontColor(isName ? NameInk : Ink)
                        .SemiBoldIf(isName));
                }
            }

            // Bold total row with a divider line above it.
            for (var i = 0; i < headers.Count; i++)
            {
                var numeric = i < numericFlags.Count && numericFlags[i];
                var text = i < totalRow.Count ? totalRow[i] ?? "" : "";
                table.Cell().Element(c => c
                    .Border(0.5f).BorderColor(GridLine)
                    .Background(TitleFill)
                    .BorderTop(2).BorderColor(Border)
                    .Padding(3, Unit.Point)
                    .AlignTo(numeric)
                    .Text(text)
                    .SemiBold().FontColor(Border));
            }
        });
    }

    /// <summary>Right-align numeric columns, left-align everything else.</summary>
    private static IContainer AlignTo(this IContainer container, bool numeric) =>
        numeric ? container.AlignRight() : container.AlignLeft();

    /// <summary>Emphasises the employee name without bolding every cell.</summary>
    private static TextBlockDescriptor SemiBoldIf(this TextBlockDescriptor text, bool condition) =>
        condition ? text.SemiBold() : text;
}

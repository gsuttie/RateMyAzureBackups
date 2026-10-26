using System.Text;

namespace RateMyAzureBackups.Services.Reports;

/// <summary>Renders the complete report as GitHub-flavored Markdown.</summary>
public static class MarkdownReportRenderer
{
    public static string Render(ReportDoc doc)
    {
        var output = new StringBuilder();
        output.Append("# ").AppendLine(Escape(doc.Title)).AppendLine();
        foreach (var block in doc.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    output.Append(new string('#', heading.Level + 1)).Append(' ').AppendLine(Escape(heading.Text));
                    break;
                case ParaBlock paragraph:
                    var text = Escape(paragraph.Text);
                    output.AppendLine(paragraph.Bold ? $"**{text}**" : text);
                    break;
                case NoteBlock note:
                    output.Append("> ").AppendLine(Escape(note.Text).Replace("\n", "\n> "));
                    break;
                case BulletBlock bullets:
                    foreach (var item in bullets.Items)
                        output.Append("- ").AppendLine(Escape(item).Replace("\n", "\n  "));
                    break;
                case TableBlock table:
                    Table(output, table);
                    break;
                case GradeBlock grade:
                    output.Append("**").Append(Escape($"{grade.Grade} - {grade.Label}")).AppendLine("**").AppendLine();
                    output.AppendLine(Escape(grade.Summary));
                    break;
                case LegendBlock:
                    Table(output, new TableBlock(["Grade", "Rating", "Description"],
                        BackupRatingService.Legend.Select(item => new[] { item.Grade, item.Label, item.Description }).ToList(),
                        "", null, false));
                    break;
                default:
                    throw new NotSupportedException($"Markdown rendering is not implemented for {block.GetType().Name}.");
            }
            output.AppendLine();
        }
        return output.ToString();
    }

    private static void Table(StringBuilder output, TableBlock table)
    {
        if (table.Rows.Count == 0)
        {
            output.AppendLine(Escape(table.EmptyText));
            return;
        }

        var width = Math.Max(table.Headers?.Length ?? 0, table.Rows.Max(row => row.Length));
        var headers = table.Headers is { Length: > 0 } ? table.Headers
            : table.FirstColHeader && width == 2 ? ["Setting", "Value"]
            : Enumerable.Range(1, width).Select(i => $"Column {i}").ToArray();
        Row(output, Enumerable.Range(0, width).Select(i => i < headers.Length ? Escape(headers[i]) : ""));
        Row(output, Enumerable.Repeat("---", width));
        foreach (var row in table.Rows)
            Row(output, Enumerable.Range(0, width).Select(i =>
            {
                var cell = i < row.Length ? Escape(row[i]).Replace("\n", "<br>") : "";
                return table.FirstColHeader && i == 0 ? $"**{cell}**" : cell;
            }));
    }

    private static void Row(StringBuilder output, IEnumerable<string> cells) =>
        output.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");

    private static string Escape(string text)
    {
        var output = new StringBuilder();
        foreach (var character in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            switch (character)
            {
                case '&': output.Append("&amp;"); break;
                case '<': output.Append("&lt;"); break;
                case '>': output.Append("&gt;"); break;
                default:
                    if ("\\`*_{}[]()#+-.!|~".Contains(character)) output.Append('\\');
                    output.Append(character);
                    break;
            }
        }
        return output.ToString();
    }
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace RemoteConnectionManager.App.Views;

/// <summary>
/// Read-only Markdown for entry and folder descriptions, in the app's dark theme. Text can be selected
/// and copied. Descriptions come from other people through the shared database, so links open only for
/// http, https and mailto, images show their alt text, and HTML shows as plain text: nothing is fetched or run.
/// Single line breaks are kept, so pasted notes look as they were typed.
/// </summary>
public sealed class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Render()));

    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .DisableHtml()
        .Build();

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        IsReadOnlyCaretVisible = false;
        Background = System.Windows.Media.Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(ForegroundProperty, "Chrome.Text");
        SetResourceReference(SelectionBrushProperty, "Chrome.Accent");
        Render();
    }

    /// <summary>The box never scrolls itself, so pass the wheel on to whatever scrolls around it.</summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Handled || VisualTreeHelper.GetParent(this) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
    }

    private void Render()
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        document.SetBinding(FlowDocument.FontFamilyProperty, new System.Windows.Data.Binding(nameof(FontFamily)) { Source = this });
        document.SetBinding(FlowDocument.FontSizeProperty, new System.Windows.Data.Binding(nameof(FontSize)) { Source = this });
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Chrome.Text");

        if (!string.IsNullOrWhiteSpace(Markdown))
        {
            foreach (var block in Markdig.Markdown.Parse(Markdown, Pipeline)) AddBlock(document.Blocks, block, tight: false);
            if (document.Blocks.FirstBlock is { } first) first.Margin = new Thickness(first.Margin.Left, 0, first.Margin.Right, first.Margin.Bottom);
            if (document.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }
        Document = document;
    }

    // ---------- blocks ----------

    /// <param name="tight">Inside a list item or table cell: less space after paragraphs.</param>
    private void AddBlock(BlockCollection into, MdBlock block, bool tight)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var p = Paragraph(heading.Inline, tight);
                p.FontWeight = FontWeights.SemiBold;
                p.FontSize = heading.Level switch { 1 => FontSize + 7, 2 => FontSize + 4, 3 => FontSize + 2, _ => FontSize };
                p.Margin = new Thickness(0, 12, 0, 4);
                into.Add(p);
                break;
            }
            case ParagraphBlock paragraph:
                into.Add(Paragraph(paragraph.Inline, tight));
                break;
            case CodeBlock code:
            {
                var lines = code.Lines.Lines.Take(code.Lines.Count).Select(l => l.ToString());
                var p = new Paragraph(new Run(string.Join("\n", lines)))
                {
                    FontSize = Math.Max(FontSize - 1, 9),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 4, 0, 10),
                    BorderThickness = new Thickness(1),
                };
                p.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                p.SetResourceReference(TextElement.BackgroundProperty, "Surface.Input");
                p.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "Chrome.Border");
                into.Add(p);
                break;
            }
            case QuoteBlock quote:
            {
                var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 2, 0, 10) };
                section.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "Chrome.Border");
                section.SetResourceReference(TextElement.ForegroundProperty, "Chrome.TextMuted");
                foreach (var child in quote) AddBlock(section.Blocks, child, tight: true);
                into.Add(section);
                break;
            }
            case ListBlock list:
            {
                var result = new List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, tight ? 2 : 10),
                    Padding = new Thickness(22, 0, 0, 0),
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) result.StartIndex = start;
                // A task list has its own tick boxes, so no bullet as well.
                if (list.Any(i => i is ListItemBlock { Count: > 0 } li && li[0] is ParagraphBlock { Inline.FirstChild: TaskList }))
                    result.MarkerStyle = TextMarkerStyle.None;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var listItem = new ListItem();
                    foreach (var child in item) AddBlock(listItem.Blocks, child, tight: true);
                    result.ListItems.Add(listItem);
                }
                into.Add(result);
                break;
            }
            case MdTable table:
                into.Add(Table(table));
                break;
            case ThematicBreakBlock:
            {
                var rule = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
                rule.SetResourceReference(Border.BackgroundProperty, "Chrome.Border");
                into.Add(new BlockUIContainer(rule) { Margin = new Thickness(0, 4, 0, 8) });
                break;
            }
            case LeafBlock leaf when leaf.Inline is not null:
                into.Add(Paragraph(leaf.Inline, tight));
                break;
            case LeafBlock leaf:
                into.Add(new Paragraph(new Run(string.Join("\n", leaf.Lines.Lines.Take(leaf.Lines.Count).Select(l => l.ToString())))) { Margin = new Thickness(0, 0, 0, tight ? 2 : 8) });
                break;
            case ContainerBlock container:
                foreach (var child in container) AddBlock(into, child, tight);
                break;
        }
    }

    private Paragraph Paragraph(ContainerInline? inline, bool tight)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, tight ? 2 : 8) };
        if (inline is not null) AddInlines(p.Inlines, inline);
        return p;
    }

    private System.Windows.Documents.Table Table(MdTable table)
    {
        var result = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10), BorderThickness = new Thickness(1, 1, 0, 0) };
        result.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "Chrome.Border");
        var columns = table.OfType<MdTableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (var i = 0; i < columns; i++) result.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        foreach (var row in table.OfType<MdTableRow>())
        {
            var tableRow = new System.Windows.Documents.TableRow();
            if (row.IsHeader)
            {
                tableRow.FontWeight = FontWeights.SemiBold;
                tableRow.SetResourceReference(TextElement.BackgroundProperty, "Chrome.PanelHeader");
            }
            foreach (var cell in row.OfType<MdTableCell>())
            {
                var tableCell = new System.Windows.Documents.TableCell { BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(8, 4, 8, 4) };
                tableCell.SetResourceReference(System.Windows.Documents.TableCell.BorderBrushProperty, "Chrome.Border");
                foreach (var child in cell) AddBlock(tableCell.Blocks, child, tight: true);
                if (tableCell.Blocks.LastBlock is { } last) last.Margin = new Thickness(0);
                tableRow.Cells.Add(tableCell);
            }
            group.Rows.Add(tableRow);
        }
        result.RowGroups.Add(group);
        return result;
    }

    // ---------- inlines ----------

    private void AddInlines(InlineCollection into, ContainerInline container)
    {
        foreach (var inline in container) AddInline(into, inline);
    }

    private void AddInline(InlineCollection into, MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                into.Add(new Run(literal.Content.ToString()));
                break;
            case EmphasisInline emphasis:
            {
                Span span = emphasis.DelimiterChar == '~'
                    ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                AddInlines(span.Inlines, emphasis);
                into.Add(span);
                break;
            }
            case CodeInline code:
            {
                var run = new Run(code.Content);
                run.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                run.SetResourceReference(TextElement.BackgroundProperty, "Surface.Control");
                into.Add(run);
                break;
            }
            case LineBreakInline:
                into.Add(new LineBreak());
                break;
            case TaskList task:
            {
                // Segoe Fluent Icons tick box glyphs, so both states are the same size.
                var box = new Run(task.Checked ? "" : "") { BaselineAlignment = BaselineAlignment.Center };
                box.SetResourceReference(TextElement.FontFamilyProperty, "Font.Glyph");
                if (task.Checked) box.SetResourceReference(TextElement.ForegroundProperty, "Chrome.Ok");
                into.Add(box);
                into.Add(new Run("  "));
                break;
            }
            case LinkInline { IsImage: true } image:
            {
                var alt = new Span();
                AddInlines(alt.Inlines, image);
                if (alt.Inlines.Count == 0) alt.Inlines.Add(new Run("image"));
                alt.Inlines.InsertBefore(alt.Inlines.FirstInline, new Run("[image: "));
                alt.Inlines.Add(new Run("]"));
                alt.SetResourceReference(TextElement.ForegroundProperty, "Chrome.TextMuted");
                into.Add(alt);
                break;
            }
            case LinkInline link:
                into.Add(Link(link.Url, link.FirstChild is null ? null : link));
                break;
            case AutolinkInline auto:
                into.Add(Link(auto.IsEmail ? "mailto:" + auto.Url : auto.Url, null, auto.Url));
                break;
            case HtmlInline html:
                into.Add(new Run(html.Tag));
                break;
            case HtmlEntityInline entity:
                into.Add(new Run(entity.Transcoded.ToString()));
                break;
            case ContainerInline container:
                AddInlines(into, container);
                break;
            default:
                into.Add(new Run(inline.ToString()));
                break;
        }
    }

    /// <summary>A clickable link for safe schemes; anything else shows as text with the address in a tooltip.</summary>
    private Span Link(string? url, ContainerInline? content, string? text = null)
    {
        var safe = Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";
        Span span = safe ? new Hyperlink { NavigateUri = uri } : new Span();
        if (content is not null) AddInlines(span.Inlines, content);
        if (span.Inlines.Count == 0) span.Inlines.Add(new Run(text ?? url ?? string.Empty));
        span.ToolTip = url;
        if (span is Hyperlink hyperlink)
        {
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "Chrome.AccentLight");
            hyperlink.RequestNavigate += OpenLink;
        }
        return span;
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser or mail program registered; nothing useful to do.
        }
    }
}

using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LeanStudio.Core.Editing;

/// <summary>A run of text in Markdown, with its style.</summary>
/// <param name="Text">The text.</param>
/// <param name="Bold">In <c>**bold**</c>.</param>
/// <param name="Italic">In <c>*italics*</c>.</param>
/// <param name="Code">In <c>`code`</c>.</param>
/// <param name="Strike">In <c>~~strikethrough~~</c>.</param>
/// <param name="Link">The address it links to, or null.</param>
public sealed record MdSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false, bool Strike = false, string? Link = null);

/// <summary>A block of a Markdown document, as a preview draws it.</summary>
public abstract record MdBlock
{
    /// <summary>The 0-based source line the block starts on, to go from the preview to the text.</summary>
    public int Line { get; init; }
}

/// <summary>A heading, level 1 to 6.</summary>
public sealed record MdHeading(int Level, IReadOnlyList<MdSpan> Spans) : MdBlock;

/// <summary>A paragraph.</summary>
public sealed record MdParagraph(IReadOnlyList<MdSpan> Spans) : MdBlock;

/// <summary>A code block, with the language its fence names (<c>lean</c>, empty for none).</summary>
public sealed record MdCode(string Language, string Text) : MdBlock;

/// <summary>A block quote.</summary>
public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

/// <summary>A list; each item a list of blocks, with its task box (<c>- [x]</c>) if it has one.</summary>
public sealed record MdList(bool Ordered, int Start, IReadOnlyList<(bool? Done, IReadOnlyList<MdBlock> Blocks)> Items) : MdBlock;

/// <summary>A table: the header row, then the others.</summary>
public sealed record MdTable(IReadOnlyList<IReadOnlyList<MdSpan>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdSpan>>> Rows) : MdBlock;

/// <summary>An image: its description and its address.</summary>
public sealed record MdImage(string Alt, string Url) : MdBlock;

/// <summary>A horizontal rule.</summary>
public sealed record MdRule : MdBlock;

/// <summary>
/// Markdown read into blocks a preview can draw without a browser: headings, paragraphs with bold, italics, code
/// and links, code blocks, quotes, lists (task lists too), tables, images and rules. Raw HTML is shown as text.
/// </summary>
public static class MarkdownModel
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    /// <summary>Read <paramref name="markdown"/>.</summary>
    public static IReadOnlyList<MdBlock> Parse(string markdown) => Blocks(Markdown.Parse(markdown, Pipeline));

    private static List<MdBlock> Blocks(ContainerBlock container)
    {
        var list = new List<MdBlock>();
        foreach (Block b in container)
        {
            MdBlock? block = b switch
            {
                HeadingBlock h => new MdHeading(h.Level, Spans(h.Inline)),
                ParagraphBlock p when OnlyImage(p.Inline) is LinkInline img => new MdImage(Plain(img), img.Url ?? ""),
                ParagraphBlock p => new MdParagraph(Spans(p.Inline)),
                FencedCodeBlock f => new MdCode(f.Info ?? "", f.Lines.ToString().TrimEnd('\n')),
                CodeBlock c => new MdCode("", c.Lines.ToString().TrimEnd('\n')),
                QuoteBlock q => new MdQuote(Blocks(q)),
                ListBlock l => new MdList(l.IsOrdered, int.TryParse(l.OrderedStart, out int start) ? start : 1,
                    l.OfType<ListItemBlock>().Select(i => (TaskOf(i), (IReadOnlyList<MdBlock>)Blocks(i))).ToList()),
                Table t => TableOf(t),
                ThematicBreakBlock => new MdRule(),
                HtmlBlock html => new MdParagraph([new MdSpan(html.Lines.ToString().TrimEnd('\n'), Code: true)]),
                _ => null,
            };
            if (block is not null)
            {
                list.Add(block with { Line = b.Line });
            }
        }
        return list;
    }

    private static MdTable TableOf(Table t)
    {
        var rows = t.OfType<TableRow>().Select(r => (IReadOnlyList<IReadOnlyList<MdSpan>>)r.OfType<TableCell>()
            .Select(c => (IReadOnlyList<MdSpan>)c.OfType<ParagraphBlock>().SelectMany(p => Spans(p.Inline)).ToList()).ToList()).ToList();
        bool header = t.OfType<TableRow>().FirstOrDefault()?.IsHeader == true;
        return new MdTable(header && rows.Count > 0 ? rows[0] : [], header ? rows.Skip(1).ToList() : rows);
    }

    /// <summary>A list item's task box: true ticked, false not, null when it has none.</summary>
    private static bool? TaskOf(ListItemBlock item) =>
        item.FirstOrDefault() is ParagraphBlock { Inline: { } inl } && inl.FirstChild is TaskList task ? task.Checked : null;

    private static LinkInline? OnlyImage(ContainerInline? inline) =>
        inline?.FirstChild is LinkInline { IsImage: true } img && img.NextSibling is null ? img : null;

    private static string Plain(ContainerInline c) => string.Concat(Spans(c).Select(s => s.Text));

    private static List<MdSpan> Spans(ContainerInline? inline)
    {
        var spans = new List<MdSpan>();
        void Walk(Inline? i, bool bold, bool italic, bool strike, string? link)
        {
            for (; i is not null; i = i.NextSibling)
            {
                switch (i)
                {
                    case TaskList:
                        break;
                    case LiteralInline lit:
                        spans.Add(new MdSpan(lit.Content.ToString(), bold, italic, false, strike, link));
                        break;
                    case CodeInline code:
                        spans.Add(new MdSpan(code.Content, bold, italic, true, strike, link));
                        break;
                    case LineBreakInline:
                        spans.Add(new MdSpan(" ", bold, italic, false, strike, link));
                        break;
                    case EmphasisInline em:
                        bool b2 = bold || (em.DelimiterChar is '*' or '_' && em.DelimiterCount >= 2);
                        bool i2 = italic || (em.DelimiterChar is '*' or '_' && em.DelimiterCount == 1);
                        bool s2 = strike || em.DelimiterChar == '~';
                        Walk(em.FirstChild, b2, i2, s2, link);
                        break;
                    case LinkInline { IsImage: true } img:
                        spans.Add(new MdSpan("[" + Plain(img) + "]", bold, italic, false, strike, img.Url));
                        break;
                    case LinkInline l:
                        Walk(l.FirstChild, bold, italic, strike, l.Url);
                        break;
                    case AutolinkInline a:
                        spans.Add(new MdSpan(a.Url, bold, italic, false, strike, a.IsEmail ? "mailto:" + a.Url : a.Url));
                        break;
                    case HtmlInline h:
                        spans.Add(new MdSpan(h.Tag, bold, italic, true, strike, link));
                        break;
                    case ContainerInline c:
                        Walk(c.FirstChild, bold, italic, strike, link);
                        break;
                }
            }
        }
        Walk(inline?.FirstChild, false, false, false, null);
        return spans;
    }
}

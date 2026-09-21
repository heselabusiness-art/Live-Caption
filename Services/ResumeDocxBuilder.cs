using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace LiveCaptionViewer.Services;

// =============================================================================
// ResumeDocxBuilder
// Clones the user's original .docx (styles, margins, numbering, theme) and
// replaces only the body with content from the AI's HTML — using paragraph
// and run properties sampled from the template, not hard-coded "Heading1".
//
// Uses AngleSharp for HTML (nested tags, lists, tables) and maps heading /
// list styles from the template document itself.
// =============================================================================

public static class ResumeDocxBuilder
{
    public static string Build(string templatePath, string responseHtml, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        string stamp  = DateTime.Now.ToString("yyyyMMdd_HHmm");
        string output = Path.Combine(outputDir, $"Resume_Optimized_{stamp}.docx");

        File.Copy(templatePath, output, overwrite: true);

        using var wdoc = WordprocessingDocument.Open(output, isEditable: true);
        var mainPart = wdoc.MainDocumentPart
                       ?? throw new InvalidOperationException("Main document part missing.");
        var document = mainPart.Document
                       ?? throw new InvalidOperationException("Document root missing.");
        var body = document.Body
                   ?? throw new InvalidOperationException("Document body missing.");

        var snapshot = TemplateSnapshot.Capture(body, wdoc);
        var sectPr   = body.Elements<SectionProperties>().LastOrDefault()
                       ?.CloneNode(true) as SectionProperties;

        body.RemoveAllChildren();

        foreach (var block in HtmlToBlocks(NormalizeAiHtml(responseHtml), snapshot))
            body.AppendChild(block);

        if (sectPr is not null)
            body.AppendChild(sectPr);

        document.Save();
        return output;
    }

    // ── HTML normalization ────────────────────────────────────────────────────

    private static string NormalizeAiHtml(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "<html><body></body></html>";

        string h = raw.Trim();
        h = Regex.Replace(h, @"^```(?:html|markdown|md)?\s*", "", RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"\s*```\s*$", "", RegexOptions.IgnoreCase);

        if (!h.Contains('<'))
        {
            var lines = h.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var inner = string.Join("", lines
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Select(l => $"<p>{WebUtility.HtmlEncode(l)}</p>"));
            return $"<html><body>{inner}</body></html>";
        }

        if (!Regex.IsMatch(h, @"<html[\s>]", RegexOptions.IgnoreCase))
            h = $"<html><body>{h}</body></html>";
        return h;
    }

    // ── Template snapshot ─────────────────────────────────────────────────────

    private sealed class TemplateSnapshot
    {
        public ParagraphProperties DefaultParagraphProps { get; init; } = new();
        public RunProperties      DefaultRunProps      { get; init; } = new();
        public ParagraphProperties?[] HeadingParagraphProps { get; init; } = new ParagraphProperties?[6];
        public NumberingProperties? ListNumberingProps { get; init; }
        public ParagraphProperties? ListParagraphProps { get; init; }

        public static TemplateSnapshot Capture(Body body, WordprocessingDocument wdoc)
        {
            var paras = body.Descendants<Paragraph>().ToList();
            var styleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in paras)
            {
                string t = p.InnerText?.Trim() ?? "";
                if (t.Length == 0) continue;

                string sid = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
                styleCounts[sid] = styleCounts.GetValueOrDefault(sid) + 1;
            }

            string defaultSid = styleCounts.Count > 0
                ? styleCounts.OrderByDescending(kv => kv.Value).First().Key
                : "Normal";

            Paragraph? refPara = paras.FirstOrDefault(p =>
            {
                string t = p.InnerText?.Trim() ?? "";
                if (t.Length == 0) return false;
                string sid = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
                return string.Equals(sid, defaultSid, StringComparison.OrdinalIgnoreCase);
            }) ?? paras.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.InnerText));

            ParagraphProperties defaultPpr = ClonePpr(refPara?.ParagraphProperties)
                                             ?? new ParagraphProperties(new ParagraphStyleId { Val = defaultSid });

            RunProperties defaultRpr = refPara?.Descendants<Run>().FirstOrDefault()?.RunProperties is { } rp
                ? (RunProperties)rp.CloneNode(true)
                : new RunProperties();

            var headingPpr = new ParagraphProperties?[6];
            foreach (var p in paras)
            {
                var ppr = p.ParagraphProperties;
                if (ppr is null) continue;
                string? sid = ppr.ParagraphStyleId?.Val?.Value;
                int? level = null;

                if (sid is not null)
                {
                    var m = Regex.Match(sid, @"(?i)heading\s*(\d)|h\s*(\d)");
                    if (m.Success)
                    {
                        if (m.Groups[1].Success)
                            level = int.Parse(m.Groups[1].Value);
                        else if (m.Groups[2].Success)
                            level = int.Parse(m.Groups[2].Value);
                    }
                    else if (Regex.IsMatch(sid, @"(?i)title|subtitle"))
                        level = 1;
                }

                if (level is null && ppr.GetFirstChild<OutlineLevel>() is { Val: not null } ol)
                {
                    int olv = (int)ol.Val.Value;
                    if (olv is >= 0 and <= 5)
                        level = olv + 1;
                }

                if (level is >= 1 and <= 6 && headingPpr[level.Value - 1] is null
                                           && !string.IsNullOrWhiteSpace(p.InnerText))
                    headingPpr[level.Value - 1] = (ParagraphProperties)ppr.CloneNode(true);
            }

            if (headingPpr[0] is null)
            {
                foreach (var p in paras)
                {
                    if (string.IsNullOrWhiteSpace(p.InnerText)) continue;
                    var r = p.Descendants<Run>().FirstOrDefault();
                    if (r?.RunProperties?.GetFirstChild<Bold>() is null) continue;
                    var sid = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                    if (string.Equals(sid, defaultSid, StringComparison.OrdinalIgnoreCase)) continue;
                    headingPpr[0] = p.ParagraphProperties is { } pp
                        ? (ParagraphProperties)pp.CloneNode(true)
                        : new ParagraphProperties(new ParagraphStyleId { Val = sid });
                    break;
                }
            }

            FillHeadingStylesFromStylesPart(wdoc, headingPpr);

            NumberingProperties? listNum = null;
            ParagraphProperties? listPpr = null;
            foreach (var p in paras)
            {
                var np = p.ParagraphProperties?.GetFirstChild<NumberingProperties>();
                if (np is null) continue;
                listNum = (NumberingProperties)np.CloneNode(true);
                if (p.ParagraphProperties is { } lp)
                {
                    listPpr = (ParagraphProperties)lp.CloneNode(true);
                    foreach (var old in listPpr.Elements<NumberingProperties>().ToList())
                        old.Remove();
                    listPpr.AppendChild((NumberingProperties)listNum.CloneNode(true));
                }
                break;
            }

            return new TemplateSnapshot
            {
                DefaultParagraphProps = defaultPpr,
                DefaultRunProps       = defaultRpr,
                HeadingParagraphProps = headingPpr,
                ListNumberingProps    = listNum,
                ListParagraphProps    = listPpr
            };
        }

        /// <summary>
        /// When the resume body never used Heading 1–4 paragraphs, scan
        /// styles.xml for built-in / word named "Heading N" and map style IDs.
        /// </summary>
        private static void FillHeadingStylesFromStylesPart(
            WordprocessingDocument wdoc,
            ParagraphProperties?[] headingPpr)
        {
            var styles = wdoc.MainDocumentPart?.StyleDefinitionsPart?.Styles;
            if (styles is null) return;

            foreach (var style in styles.Elements<Style>())
            {
                if (style.Type?.Value != StyleValues.Paragraph) continue;
                string? displayName = style.GetFirstChild<StyleName>()?.Val?.Value;
                if (string.IsNullOrEmpty(displayName)) continue;

                var m = Regex.Match(displayName, @"(?i)heading\s*(\d)");
                if (!m.Success) continue;

                int level = int.Parse(m.Groups[1].Value);
                if (level is < 1 or > 6) continue;
                if (headingPpr[level - 1]   is not null) continue;

                string? styleId = style.StyleId?.Value;
                if (string.IsNullOrEmpty(styleId)) continue;

                headingPpr[level - 1] =
                    new ParagraphProperties(new ParagraphStyleId { Val = styleId });
            }
        }

        private static ParagraphProperties? ClonePpr(ParagraphProperties? p) =>
            p is null ? null : (ParagraphProperties)p.CloneNode(true);
    }

    // ── HTML → OpenXml ─────────────────────────────────────────────────────────

    private static IEnumerable<OpenXmlElement> HtmlToBlocks(string html, TemplateSnapshot snap)
    {
        var parser = new HtmlParser();
        var doc    = parser.ParseDocument(html);
        IElement? root = doc.Body ?? doc.DocumentElement;
        if (root is null) yield break;

        foreach (var child in root.ChildNodes)
        {
            if (child is IElement el)
            {
                foreach (var x in ProcessElement(el, snap))
                    yield return x;
            }
        }
    }

    private static string ElText(IElement el) =>
        Regex.Replace(el.TextContent ?? "", @"\s+", " ").Trim();

    private static IEnumerable<OpenXmlElement> ProcessElement(IElement el, TemplateSnapshot snap)
    {
        string tag = el.LocalName.ToLowerInvariant();

        if (tag is "script" or "style" or "noscript" or "svg" or "template")
            yield break;

        if (tag is "br")
        {
            yield return new Paragraph(new Run(
                (RunProperties)snap.DefaultRunProps.CloneNode(true),
                new Break(),
                new Text(" ") { Space = SpaceProcessingModeValues.Preserve }));
            yield break;
        }

        if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
        {
            int level = tag[1] - '0';
            var para = BuildHeadingParagraph(el, snap, level);
            if (para is not null) yield return para;
            yield break;
        }

        if (tag is "p")
        {
            var para = BuildBodyParagraph(el, snap);
            if (para is not null) yield return para;
            yield break;
        }

        if (tag is "hr")
        {
            yield return HorizontalRule(snap);
            yield break;
        }

        if (tag is "blockquote")
        {
            var para = BuildBodyParagraph(el, snap);
            if (para is not null)
            {
                var pPr = para.ParagraphProperties ?? new ParagraphProperties();
                if (para.ParagraphProperties is null) para.PrependChild(pPr);
                pPr.AppendChild(new Indentation { Left = "720", Right = "720" });
                yield return para;
            }
            yield break;
        }

        if (tag is "ul" or "ol")
        {
            foreach (var li in el.Children.Where(c =>
                         string.Equals(c.LocalName, "li", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var item in BuildListItem(li, snap, tag == "ol"))
                    yield return item;
            }
            yield break;
        }

        if (tag is "table")
        {
            foreach (var row in el.QuerySelectorAll("tr"))
            {
                var cells = row.QuerySelectorAll("th,td")
                    .Select(c => ElText(c).Trim())
                    .Where(s => s.Length > 0);
                string line = string.Join("    ", cells);
                if (line.Length > 0)
                    yield return BuildSimpleParagraph(snap, line);
            }
            yield break;
        }

        if (tag is "div" or "section" or "article" or "main" or "header" or "footer"
                 or "aside" or "nav" or "figure" or "figcaption" or "center" or "form")
        {
            bool yielded = false;
            foreach (var sub in el.Children)
            {
                foreach (var x in ProcessElement(sub, snap))
                {
                    yielded = true;
                    yield return x;
                }
            }
            if (!yielded)
            {
                string t = ElText(el);
                if (t.Length > 0)
                    yield return BuildSimpleParagraph(snap, t);
            }
            yield break;
        }

        if (tag is "li")
        {
            foreach (var x in BuildListItem(el, snap, ordered: false))
                yield return x;
            yield break;
        }

        {
            string t = ElText(el);
            if (t.Length > 0 && !el.Children.Any())
                yield return BuildSimpleParagraph(snap, t);
            else
            {
                foreach (var sub in el.Children)
                    foreach (var x in ProcessElement(sub, snap))
                        yield return x;
            }
        }
    }

    private static Paragraph? BuildHeadingParagraph(IElement el, TemplateSnapshot snap, int level)
    {
        level = Math.Clamp(level, 1, 6);
        var pPr = snap.HeadingParagraphProps[level - 1] is { } hp
            ? (ParagraphProperties)hp.CloneNode(true)
            : (ParagraphProperties)snap.DefaultParagraphProps.CloneNode(true);

        var p = new Paragraph(pPr);
        bool forceBold = snap.HeadingParagraphProps[level - 1] is null;

        foreach (var run in InlineRunsFromElement(el, snap))
        {
            if (forceBold)
            {
                var r = (Run)run.CloneNode(true);
                var rPr = r.GetFirstChild<RunProperties>();
                if (rPr is null)
                {
                    rPr = (RunProperties)snap.DefaultRunProps.CloneNode(true);
                    r.PrependChild(rPr);
                }
                if (rPr.GetFirstChild<Bold>() is null)
                    rPr.AppendChild(new Bold());
                p.AppendChild(r);
            }
            else
                p.AppendChild(run);
        }

        return p.Descendants<Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text)) ? p : null;
    }

    private static Paragraph? BuildBodyParagraph(IElement el, TemplateSnapshot snap)
    {
        var p = new Paragraph((ParagraphProperties)snap.DefaultParagraphProps.CloneNode(true));
        foreach (var run in InlineRunsFromElement(el, snap))
            p.AppendChild(run);
        return p.Descendants<Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text)) ? p : null;
    }

    private static Paragraph BuildSimpleParagraph(TemplateSnapshot snap, string text)
    {
        var p = new Paragraph((ParagraphProperties)snap.DefaultParagraphProps.CloneNode(true));
        var rp = (RunProperties)snap.DefaultRunProps.CloneNode(true);
        p.AppendChild(new Run(rp, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return p;
    }

    private static IEnumerable<Paragraph> BuildListItem(IElement li, TemplateSnapshot snap, bool ordered)
    {
        var blocks = li.Children.ToList();
        if (blocks.Count == 0)
        {
            var single = BuildListParagraph(li, snap, ordered);
            if (single is not null) yield return single;
            yield break;
        }

        bool first = true;
        foreach (var ch in blocks)
        {
            string name = ch.LocalName.ToLowerInvariant();
            if (name is "ul" or "ol")
            {
                foreach (var nested in ch.Children.Where(c =>
                             string.Equals(c.LocalName, "li", StringComparison.OrdinalIgnoreCase)))
                foreach (var p in BuildListItem(nested, snap, name == "ol"))
                    yield return p;
                continue;
            }

            if (name is "p" or "div")
            {
                var inner = BuildBodyParagraph(ch, snap);
                if (inner is null) continue;
                PromoteToListItem(inner, snap, ordered, suppressBullet: !first);
                first = false;
                yield return inner;
                continue;
            }

            foreach (var x in ProcessElement(ch, snap))
            {
                if (x is Paragraph lp)
                {
                    PromoteToListItem(lp, snap, ordered, suppressBullet: !first);
                    first = false;
                    yield return lp;
                }
                else if (x is Run rx)
                {
                    var wrap = new Paragraph((ParagraphProperties)snap.DefaultParagraphProps.CloneNode(true));
                    wrap.AppendChild(rx);
                    PromoteToListItem(wrap, snap, ordered, suppressBullet: !first);
                    first = false;
                    yield return wrap;
                }
            }
        }
    }

    private static Paragraph? BuildListParagraph(IElement li, TemplateSnapshot snap, bool ordered)
    {
        var basePpr = snap.ListParagraphProps is not null
            ? (ParagraphProperties)snap.ListParagraphProps.CloneNode(true)
            : (ParagraphProperties)snap.DefaultParagraphProps.CloneNode(true);

        var p = new Paragraph(basePpr);

        if (snap.ListNumberingProps is null)
        {
            foreach (var np in basePpr.Elements<NumberingProperties>().ToList())
                np.Remove();
            basePpr.AppendChild(new Indentation { Left = "360", Hanging = "360" });
            p.AppendChild(new Run(
                (RunProperties)snap.DefaultRunProps.CloneNode(true),
                new Text("• ") { Space = SpaceProcessingModeValues.Preserve }));
        }

        foreach (var run in InlineRunsFromElement(li, snap))
            p.AppendChild(run);
        return p.Descendants<Text>().Any() ? p : null;
    }

    private static void PromoteToListItem(Paragraph p, TemplateSnapshot snap, bool ordered, bool suppressBullet)
    {
        var pPr = p.ParagraphProperties ?? new ParagraphProperties();
        if (p.ParagraphProperties is null)
            p.PrependChild(pPr);
        else
            pPr = p.ParagraphProperties;

        foreach (var np in pPr.Elements<NumberingProperties>().ToList())
            np.Remove();

        if (snap.ListNumberingProps is not null)
            pPr.AppendChild((NumberingProperties)snap.ListNumberingProps.CloneNode(true));
        else if (!suppressBullet)
            pPr.AppendChild(new Indentation { Left = "360", Hanging = "360" });

        if (snap.ListNumberingProps is null && !suppressBullet
            && p.Descendants<Run>().FirstOrDefault() is { } firstRun
            && !firstRun.InnerText.StartsWith("•", StringComparison.Ordinal))
        {
            firstRun.InsertBeforeSelf(new Run(
                (RunProperties)snap.DefaultRunProps.CloneNode(true),
                new Text("• ") { Space = SpaceProcessingModeValues.Preserve }));
        }
    }

    private static IEnumerable<Run> InlineRunsFromElement(IElement el, TemplateSnapshot snap)
    {
        bool any = false;
        foreach (var node in el.ChildNodes)
        {
            switch (node)
            {
                case IText text:
                {
                    string t = WebUtility.HtmlDecode(text.Data);
                    if (t.Length == 0) break;
                    any = true;
                    yield return new Run(
                        (RunProperties)snap.DefaultRunProps.CloneNode(true),
                        new Text(t) { Space = SpaceProcessingModeValues.Preserve });
                    break;
                }
                case IElement sub:
                {
                    string n = sub.LocalName.ToLowerInvariant();
                    if (n is "br")
                    {
                        any = true;
                        yield return new Run(
                            (RunProperties)snap.DefaultRunProps.CloneNode(true),
                            new Break());
                        break;
                    }
                    if (n is "strong" or "b" or "em" or "i" or "u" or "a" or "span" or "code")
                    {
                        var rp = (RunProperties)snap.DefaultRunProps.CloneNode(true);
                        if (n is "strong" or "b") rp.AppendChild(new Bold());
                        if (n is "em" or "i")     rp.AppendChild(new Italic());
                        if (n is "u")             rp.AppendChild(new Underline { Val = UnderlineValues.Single });
                        string inner = ElText(sub);
                        if (inner.Length > 0)
                        {
                            any = true;
                            yield return new Run(rp, new Text(inner) { Space = SpaceProcessingModeValues.Preserve });
                        }
                        break;
                    }
                    foreach (var r in InlineRunsFromElement(sub, snap))
                    {
                        any = true;
                        yield return r;
                    }
                    break;
                }
            }
        }

        if (!any && ElText(el).Length > 0)
            yield return new Run(
                (RunProperties)snap.DefaultRunProps.CloneNode(true),
                new Text(ElText(el)) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Paragraph HorizontalRule(TemplateSnapshot snap)
    {
        var pPr = (ParagraphProperties)snap.DefaultParagraphProps.CloneNode(true);
        var pBdr = new ParagraphBorders();
        pBdr.AppendChild(new BottomBorder
        {
            Val   = BorderValues.Single,
            Size  = 6,
            Space = 1,
            Color = "auto"
        });
        pPr.AppendChild(pBdr);
        return new Paragraph(pPr);
    }
}

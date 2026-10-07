namespace LiveCaptionViewer.Services;

/// <summary>
/// DOM-structure-independent response readers. They do not rely on ChatGPT/Claude
/// container selectors, so they keep working when the site's markup changes.
/// </summary>
internal static class PageJsonScanScripts
{
    /// <summary>
    /// Scans the visible page text (then raw textContent) from the bottom up and returns
    /// the LAST substring that is a balanced AND valid JSON object. Template text from our
    /// own prompt (`"key": string`) is not valid JSON, so it is skipped automatically.
    /// </summary>
    internal const string LastJsonObject = """
        (function() {
            const LOOSE = false;
            function scan(text) {
                if (!text) return null;
                if (text.length > 3000000) text = text.slice(text.length - 3000000);

                const re = /\{\s*"/g;
                const starts = [];
                let m;
                while ((m = re.exec(text)) !== null) starts.push(m.index);

                let tried = 0;
                for (let s = starts.length - 1; s >= 0 && tried < 80; s--) {
                    tried++;
                    const start = starts[s];
                    let depth = 0, inStr = false, esc = false, end = -1;
                    for (let i = start; i < text.length; i++) {
                        const c = text[i];
                        if (inStr) {
                            if (esc) esc = false;
                            else if (c === '\\') esc = true;
                            else if (c === '"') inStr = false;
                            continue;
                        }
                        if (c === '"') inStr = true;
                        else if (c === '{') depth++;
                        else if (c === '}') { depth--; if (depth === 0) { end = i; break; } }
                    }
                    if (end < 0) continue;

                    const cand = text.slice(start, end + 1);
                    try {
                        const obj = JSON.parse(cand);
                        if (obj && typeof obj === 'object' && !Array.isArray(obj)
                            && Object.keys(obj).length >= 1) {
                            return cand;
                        }
                    } catch (e) {
                        // Not strictly valid (e.g. raw line breaks inside a string value).
                        // Loose mode accepts a balanced object whose first value is a string
                        // — our own prompt template ("key": string) never matches this.
                        if (LOOSE && /^\{\s*"[^"]+"\s*:\s*"/.test(cand)) return cand;
                    }
                }
                return null;
            }

            const body = document.body;
            if (!body) return null;
            return scan(body.innerText) || scan(body.textContent);
        })()
        """;

    /// <summary>Same scan, but also accepts balanced-yet-not-strictly-valid JSON objects.</summary>
    internal static readonly string LastJsonObjectLoose =
        LastJsonObject.Replace("const LOOSE = false;", "const LOOSE = true;", StringComparison.Ordinal);

    /// <summary>Counts of the selectors we rely on — included in the error message for diagnosis.</summary>
    internal const string DomDiagnostics = """
        (function() {
            const q = (s) => { try { return document.querySelectorAll(s).length; } catch (e) { return -1; } };
            return JSON.stringify({
                roleAssistant: q('[data-message-author-role="assistant"]'),
                roleUser: q('[data-message-author-role="user"]'),
                turnAssistant: q('[data-turn="assistant"]'),
                turns: q('[data-testid^="conversation-turn"]'),
                articles: q('article'),
                pre: q('pre'),
                code: q('code'),
                claude: q('.font-claude-message'),
                bodyChars: (document.body && document.body.innerText || '').length,
                jsonOpen: ((document.body && document.body.innerText || '').match(/\{\s*"/g) || []).length
            });
        })()
        """;
}

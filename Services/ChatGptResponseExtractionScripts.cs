namespace LiveCaptionViewer.Services;

/// <summary>JavaScript helpers for reading ChatGPT responses (shadow DOM + copy fallback).</summary>
internal static class ChatGptResponseExtractionScripts
{
    /// <summary>JS body: given minIndex, returns longest assistant text from that index onward.</summary>
    internal const string ExtractAssistantText = """
        function walkTree(root, visit) {
            if (!root) return;
            visit(root);
            if (root.shadowRoot) walkTree(root.shadowRoot, visit);
            const kids = root.children;
            if (kids) {
                for (let i = 0; i < kids.length; i++) walkTree(kids[i], visit);
            }
        }

        function textOf(el) {
            if (!el) return '';
            return (el.innerText || el.textContent || '').trim();
        }

        function collectAssistantContainers() {
            const blocks = [];
            const seen = new Set();

            walkTree(document.body, (node) => {
                if (!node || node.nodeType !== 1 || !node.getAttribute) return;
                const role = node.getAttribute('data-message-author-role');
                if (role === 'assistant' && !seen.has(node)) {
                    seen.add(node);
                    blocks.push(node);
                }
            });

            if (!blocks.length) {
                walkTree(document.body, (node) => {
                    if (!node || node.nodeType !== 1 || !node.getAttribute) return;
                    if (node.getAttribute('data-testid') === 'assistant-message' && !seen.has(node)) {
                        seen.add(node);
                        blocks.push(node);
                    }
                });
            }

            if (!blocks.length) {
                document.querySelectorAll('article').forEach((article) => {
                    if (seen.has(article)) return;
                    const hasAssistant = article.querySelector('[data-message-author-role="assistant"]')
                        || article.getAttribute('data-turn') === 'assistant';
                    if (hasAssistant) {
                        seen.add(article);
                        blocks.push(article);
                    }
                });
            }

            return blocks;
        }

        function textFromJsonCodeBlock(container) {
            if (!container) return '';
            let best = '';
            walkTree(container, (node) => {
                if (!node || node.nodeType !== 1) return;
                const tag = (node.tagName || '').toLowerCase();
                if (tag !== 'pre' && tag !== 'code') return;
                const cls = (node.className && node.className.toString().toLowerCase()) || '';
                const t = textOf(node);
                if (!t || t.indexOf('{') < 0) return;
                const jsonish = cls.indexOf('json') >= 0 || tag === 'pre';
                if (jsonish && t.length > best.length) best = t;
            });
            return best;
        }

        function newestJsonBlockInDocument() {
            let best = '';
            walkTree(document.body, (node) => {
                if (!node || node.nodeType !== 1) return;
                const tag = (node.tagName || '').toLowerCase();
                if (tag !== 'pre' && tag !== 'code') return;
                const t = textOf(node);
                if (!t || t.indexOf('{') < 0 || t.indexOf('}') < 0) return;
                if (t.length > best.length) best = t;
            });
            return best.length ? best : null;
        }

        function extractAssistantText(minIndex) {
            const blocks = collectAssistantContainers();
            const idx = Math.max(0, minIndex | 0);
            let pickFrom = blocks;
            if (blocks.length > idx) {
                pickFrom = blocks.slice(idx);
            } else if (blocks.length) {
                pickFrom = [blocks[blocks.length - 1]];
            } else {
                const turns = document.querySelectorAll('[data-testid="conversation-turn"]');
                if (turns.length) {
                    const turn = turns[turns.length - 1];
                    const t = textOf(turn);
                    if (t.length) return t;
                }
                const main = document.querySelector('main');
                if (main) {
                    const articles = main.querySelectorAll('article');
                    if (articles.length) {
                        const t = textOf(articles[articles.length - 1]);
                        if (t.length) return t;
                    }
                }
                return null;
            }

            let best = '';
            for (let i = pickFrom.length - 1; i >= 0; i--) {
                const container = pickFrom[i];
                const jsonBlock = textFromJsonCodeBlock(container);
                if (jsonBlock.length > best.length) best = jsonBlock;
            }
            if (best.length) return best;

            for (const container of pickFrom) {
                const inner = container.querySelector('[data-message-author-role="assistant"]') || container;
                const md = inner.querySelector('[class*="markdown"]')
                    || inner.querySelector('.prose')
                    || inner.querySelector('pre')
                    || inner.querySelector('[class*="message"]')
                    || inner;
                const t = textOf(md);
                if (t.length > best.length) best = t;
            }
            if (best.length) return best;
            return newestJsonBlockInDocument();
        }

        function countAssistantContainers() {
            return collectAssistantContainers().length;
        }
        """;
}

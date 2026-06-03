// DevKit — JS helpers

(function () {
    'use strict';

    var FOCUS_DELAY_MS = 30;
    var INITIAL_RENDER_DELAY_MS = 100;
    var CLICK_BIND_DELAY_MS = 10;

    // ─── Markdown rendering ───

    function escapeHtml(text) {
        return String(text)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    // Block javascript:, data:, vbscript: and similar dangerous schemes. Relative
    // URLs and anchors (no scheme) are allowed; absolute URLs must be on the allowlist.
    function hasScheme(url) { return /^[a-z][a-z0-9+.-]*:/i.test(url); }

    function safeLinkUrl(url) {
        var u = (url || '').trim();
        if (hasScheme(u) && !/^(https?|mailto):/i.test(u)) return '';
        return u;
    }

    function safeImageUrl(url) {
        var u = (url || '').trim();
        if (hasScheme(u) && !/^https?:/i.test(u)) return '';
        return u;
    }

    function renderInline(text) {
        var s = escapeHtml(text);
        s = s.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, function (_, alt, url) {
            var safe = safeImageUrl(url);
            return safe ? '<img src="' + safe + '" alt="' + alt + '">' : alt;
        });
        s = s.replace(/\[([^\]]+)\]\(([^)]+)\)/g, function (_, label, url) {
            var safe = safeLinkUrl(url);
            return safe
                ? '<a href="' + safe + '" target="_blank" rel="noopener noreferrer">' + label + '</a>'
                : label;
        });
        s = s.replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>');
        s = s.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
        s = s.replace(/\*(.+?)\*/g, '<em>$1</em>');
        s = s.replace(/~~(.+?)~~/g, '<del>$1</del>');
        s = s.replace(/`([^`]+)`/g, '<code>$1</code>');
        s = s.replace(/\n/g, '<br>');
        return s;
    }

    var CODE_BLOCK_TOKEN = /^\x00CB(\d+)\x00$/;

    function extractCodeBlocks(src, store) {
        return src.replace(/^```(\w*)\n([\s\S]*?)^```/gm, function (_, lang, code) {
            store.push('<pre><code>' + escapeHtml(code.replace(/\n$/, '')) + '</code></pre>');
            return '\x00CB' + (store.length - 1) + '\x00';
        });
    }

    function isHeading(line) { return /^#{1,6}\s/.test(line); }
    function isHorizontalRule(line) { return /^(\*{3,}|-{3,}|_{3,})\s*$/.test(line.trim()); }
    function isTableStart(lines, idx) {
        return /^\|/.test(lines[idx].trim())
            && idx + 1 < lines.length
            && /^\|[\s:|-]+\|/.test(lines[idx + 1].trim());
    }
    function isBlockquote(line) { return /^>\s?/.test(line); }
    function isUnorderedItem(line) { return /^[\s]*[-*+]\s/.test(line); }
    function isOrderedItem(line) { return /^[\s]*\d+\.\s/.test(line); }

    function renderTable(lines, start) {
        var rows = [];
        var idx = start;
        while (idx < lines.length && /^\|/.test(lines[idx].trim())) { rows.push(lines[idx]); idx++; }
        var splitCells = function (row) {
            return row.replace(/^\|/, '').replace(/\|$/, '').split('|').map(function (c) { return c.trim(); });
        };
        var headers = splitCells(rows[0]);
        var html = '<table><thead><tr>';
        headers.forEach(function (h) { html += '<th>' + renderInline(h) + '</th>'; });
        html += '</tr></thead><tbody>';
        for (var r = 2; r < rows.length; r++) {
            var cells = splitCells(rows[r]);
            html += '<tr>';
            headers.forEach(function (_, ci) { html += '<td>' + renderInline(cells[ci] || '') + '</td>'; });
            html += '</tr>';
        }
        return { html: html + '</tbody></table>', next: idx };
    }

    function renderBlockquote(lines, start) {
        var quoted = [];
        var idx = start;
        while (idx < lines.length && isBlockquote(lines[idx])) {
            quoted.push(lines[idx].replace(/^>\s?/, ''));
            idx++;
        }
        return { html: '<blockquote>' + quoted.map(renderInline).join('<br>') + '</blockquote>', next: idx };
    }

    function renderListItem(item) {
        if (/^\[x\]/i.test(item))
            return '<li style="list-style:none;margin-left:-1.5em;"><input type="checkbox" checked disabled> ' + renderInline(item.substring(3).trim()) + '</li>';
        if (/^\[ \]/.test(item))
            return '<li style="list-style:none;margin-left:-1.5em;"><input type="checkbox" disabled> ' + renderInline(item.substring(3).trim()) + '</li>';
        return '<li>' + renderInline(item) + '</li>';
    }

    function renderUnorderedList(lines, start) {
        var html = '<ul>';
        var idx = start;
        while (idx < lines.length && isUnorderedItem(lines[idx])) {
            html += renderListItem(lines[idx].replace(/^[\s]*[-*+]\s/, ''));
            idx++;
        }
        return { html: html + '</ul>', next: idx };
    }

    function renderOrderedList(lines, start) {
        var html = '<ol>';
        var idx = start;
        while (idx < lines.length && isOrderedItem(lines[idx])) {
            html += '<li>' + renderInline(lines[idx].replace(/^[\s]*\d+\.\s/, '')) + '</li>';
            idx++;
        }
        return { html: html + '</ol>', next: idx };
    }

    function isParagraphBreak(line) {
        return line.trim() === '' || isHeading(line) || isHorizontalRule(line)
            || /^\|/.test(line.trim()) || isBlockquote(line)
            || isUnorderedItem(line) || isOrderedItem(line)
            || /^\x00CB/.test(line.trim());
    }

    function renderParagraph(lines, start) {
        var buffer = [];
        var idx = start;
        while (idx < lines.length && !isParagraphBreak(lines[idx])) { buffer.push(lines[idx]); idx++; }
        var html = buffer.length ? '<p>' + renderInline(buffer.join('\n')) + '</p>' : '';
        return { html: html, next: idx };
    }

    function renderBlock(lines, idx, codeBlocks) {
        var line = lines[idx];
        var token = line.trim().match(CODE_BLOCK_TOKEN);
        if (token) return { html: codeBlocks[parseInt(token[1], 10)], next: idx + 1 };

        var heading = line.match(/^(#{1,6})\s+(.+)$/);
        if (heading) {
            var level = heading[1].length;
            return { html: '<h' + level + '>' + renderInline(heading[2]) + '</h' + level + '>', next: idx + 1 };
        }
        if (isHorizontalRule(line)) return { html: '<hr>', next: idx + 1 };
        if (isTableStart(lines, idx)) return renderTable(lines, idx);
        if (isBlockquote(line)) return renderBlockquote(lines, idx);
        if (isUnorderedItem(line)) return renderUnorderedList(lines, idx);
        if (isOrderedItem(line)) return renderOrderedList(lines, idx);
        return renderParagraph(lines, idx);
    }

    function renderMarkdown(src) {
        var el = document.getElementById('mdPreview');
        if (!el || !src) { if (el) el.innerHTML = ''; return; }

        var codeBlocks = [];
        var normalized = extractCodeBlocks(src.replace(/\r\n/g, '\n').replace(/\r/g, '\n'), codeBlocks);
        var lines = normalized.split('\n');

        var html = '';
        var i = 0;
        while (i < lines.length) {
            if (lines[i].trim() === '') { i++; continue; }
            var block = renderBlock(lines, i, codeBlocks);
            html += block.html;
            i = block.next;
        }
        html = html.replace(/\x00CB(\d+)\x00/g, function (_, idx) { return codeBlocks[parseInt(idx, 10)]; });
        el.innerHTML = html;
    }

    // ─── Public API ───

    var clickOutsideHandlers = new Map();
    var markdownViewerInitialized = false;

    window.devKit = {
        copyToClipboard: async function (text) {
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch {
                const ta = document.createElement('textarea');
                ta.value = text;
                ta.style.position = 'fixed';
                ta.style.opacity = '0';
                document.body.appendChild(ta);
                ta.select();
                document.execCommand('copy');
                document.body.removeChild(ta);
                return true;
            }
        },

        downloadFile: function (filename, contentType, base64) {
            const link = document.createElement('a');
            link.download = filename;
            link.href = `data:${contentType};base64,${base64}`;
            link.click();
        },

        scrollToBottom: function (elementId) {
            const el = document.getElementById(elementId);
            if (el) el.scrollTop = el.scrollHeight;
        },

        focusEl: function (el) {
            if (el) setTimeout(() => el.focus(), FOCUS_DELAY_MS);
        },

        initMarkdownViewer: function () {
            var editor = document.getElementById('mdEditor');
            if (editor) {
                setTimeout(function () { renderMarkdown(editor.value); }, INITIAL_RENDER_DELAY_MS);
            }
            // Attach the file-input listener only once; this runs on every navigation
            // back to the viewer and would otherwise stack duplicate handlers.
            if (markdownViewerInitialized) return;
            var fileInput = document.getElementById('mdFileInput');
            if (fileInput) {
                fileInput.addEventListener('change', function () {
                    var file = fileInput.files[0];
                    if (!file) return;
                    var reader = new FileReader();
                    reader.onload = function (ev) {
                        var ed = document.getElementById('mdEditor');
                        if (ed) {
                            ed.value = ev.target.result;
                            ed.dispatchEvent(new Event('input', { bubbles: true }));
                        }
                    };
                    reader.readAsText(file);
                    fileInput.value = '';
                });
                markdownViewerInitialized = true;
            }
        },

        triggerFileInput: function (id) {
            var el = document.getElementById(id);
            if (el) el.click();
        },

        renderMarkdown: renderMarkdown,

        registerClickOutside: function (el, dotNetRef) {
            const handler = (e) => {
                if (el && !el.contains(e.target)) {
                    dotNetRef.invokeMethodAsync('CloseDropdown');
                }
            };
            clickOutsideHandlers.set(el, handler);
            setTimeout(() => document.addEventListener('click', handler), CLICK_BIND_DELAY_MS);
        },

        unregisterClickOutside: function (el) {
            const handler = clickOutsideHandlers.get(el);
            if (handler) {
                document.removeEventListener('click', handler);
                clickOutsideHandlers.delete(el);
            }
        }
    };
})();

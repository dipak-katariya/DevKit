// DevKit — The Markdown Viewer page: editing, preview, import and export.
// Rendering itself lives in markdown.js, reached through window.devKitMd.render.

(function () {
    'use strict';

    var RENDER_DEBOUNCE_MS = 50;

    // The parser, and the escaping it applies to every link, image and span of text, live in
    // markdown.js. This file never builds markup of its own.
    function parseMarkdown(src) { return window.devKitMd.render(src); }

    // ════════════════════════════════
    //  MARKDOWN VIEWER CONTROLLER
    // ════════════════════════════════
    var renderTimer = null;
    var exportMenuCloserBound = false;

    var DEFAULT_MD = [
        '# Welcome to Markdown Viewer',
        '',
        'A **live** Markdown editor with GitHub-style preview. No internet required.',
        '',
        '## Features',
        '',
        '- **Live preview** as you type',
        '- GitHub-flavored Markdown (tables, task lists, ~~strikethrough~~)',
        '- Syntax highlighting for code blocks',
        '- Math rendering (inline and block)',
        '- Import and export (.md, .html, PDF)',
        '- Draggable split-pane resizer',
        '',
        '## Code Example',
        '',
        '```javascript',
        'function fibonacci(n) {',
        '  if (n <= 1) return n;',
        '  return fibonacci(n - 1) + fibonacci(n - 2);',
        '}',
        'console.log(fibonacci(10)); // 55',
        '```',
        '',
        '## Math',
        '',
        'Inline math: $E = mc^2$',
        '',
        '## Table',
        '',
        '| Feature | Status |',
        '|---------|--------|',
        '| Markdown parsing | Done |',
        '| Syntax highlighting | Done |',
        '| Import / Export | Done |',
        '',
        '## Task List',
        '',
        '- [x] Set up editor',
        '- [x] Add live preview',
        '- [ ] Add more themes',
        '',
        '> "The best way to predict the future is to invent it." — Alan Kay',
        '',
        '---',
        '',
        '*Start editing to see live changes.*'
    ].join('\n');

    function setText(id, value) { var el = document.getElementById(id); if (el) el.textContent = value; }

    function updateStats(text) {
        setText('mdStatLines', 'Ln ' + (text ? text.split('\n').length : 0));
        setText('mdStatWords', 'Words ' + (text.trim() ? text.trim().split(/\s+/).length : 0));
        setText('mdStatChars', 'Chars ' + text.length);
    }

    function updatePreview() {
        var editor = document.getElementById('mdEditor');
        var preview = document.getElementById('mdPreview');
        if (!editor || !preview) return;
        preview.innerHTML = parseMarkdown(editor.value);
        updateStats(editor.value);
    }

    function scheduleRender() {
        clearTimeout(renderTimer);
        renderTimer = setTimeout(updatePreview, RENDER_DEBOUNCE_MS);
    }

    function mdDownload(content, filename, mime) {
        var blob = new Blob([content], { type: mime + ';charset=utf-8' });
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url; a.download = filename;
        document.body.appendChild(a); a.click(); document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    }

    var EXPORT_CSS =
        'body{font-family:Segoe UI,sans-serif;background:#0d1117;color:#e6edf3;padding:40px;max-width:900px;margin:0 auto;line-height:1.7;font-size:16px;}'
        + 'h1,h2{border-bottom:1px solid #30363d;padding-bottom:.3em;}h1{font-size:2em;}h2{font-size:1.5em;}h3{font-size:1.25em;}'
        + 'a{color:#58a6ff;}strong{font-weight:700;}'
        + 'code{background:rgba(110,118,129,.25);padding:.2em .4em;border-radius:4px;font-family:Consolas,monospace;font-size:85%;}'
        + 'pre{background:#161b22;border:1px solid #30363d;border-radius:8px;padding:16px;overflow-x:auto;font-size:13px;line-height:1.5;}'
        + 'pre code{background:transparent;padding:0;}'
        + 'blockquote{border-left:3px solid #30363d;color:#8b949e;padding:0 1em;margin:0 0 16px;}'
        + 'table{border-collapse:collapse;margin-bottom:16px;}th,td{border:1px solid #30363d;padding:6px 13px;}th{background:#252526;}'
        + 'tr{background:#161b22;}tr:nth-child(2n){background:#1c2128;}'
        + 'img{max-width:100%;}hr{border:0;height:2px;background:#30363d;margin:24px 0;}'
        + '.kw{color:#ff7b72;}.str{color:#a5d6ff;}.num{color:#79c0ff;}.cm{color:#8b949e;font-style:italic;}.fn{color:#d2a8ff;}.type{color:#ffa657;}.tag{color:#7ee787;}.attr{color:#79c0ff;}.val{color:#a5d6ff;}';

    function exportHtml() {
        updatePreview();
        var preview = document.getElementById('mdPreview');
        if (!preview) return;
        var full = '<!DOCTYPE html>\n<html lang="en">\n<head>\n<meta charset="UTF-8">\n<meta name="viewport" content="width=device-width,initial-scale=1.0">\n<title>Markdown Export</title>\n<style>' + EXPORT_CSS + '</style>\n</head>\n<body>\n' + preview.innerHTML + '\n</body>\n</html>';
        mdDownload(full, 'document.html', 'text/html');
    }

    function exportPdf() {
        updatePreview();
        var preview = document.getElementById('mdPreview');
        if (!preview) return;
        var win = window.open('', '_blank');
        if (!win) { alert('Please allow pop-ups to export PDF.'); return; }
        var css = '<style>body{font-family:Segoe UI,sans-serif;color:#222;padding:32px;max-width:900px;margin:0 auto;line-height:1.7;font-size:14px;}'
            + 'h1,h2{border-bottom:1px solid #ddd;padding-bottom:.3em;}h1{font-size:2em;}h2{font-size:1.5em;}'
            + 'a{color:#0969da;}code{background:#f0f0f0;padding:.2em .4em;border-radius:4px;font-family:Consolas,monospace;font-size:85%;}'
            + 'pre{background:#f6f8fa;border:1px solid #ddd;border-radius:6px;padding:16px;overflow-x:auto;font-size:85%;}pre code{background:transparent;padding:0;}'
            + 'blockquote{border-left:3px solid #ddd;color:#666;padding:0 1em;margin:0 0 16px;}'
            + 'table{border-collapse:collapse;margin-bottom:16px;}th,td{border:1px solid #ddd;padding:6px 13px;}th{background:#f6f8fa;}'
            + 'img{max-width:100%;}hr{border:0;height:2px;background:#ddd;margin:24px 0;}@media print{body{padding:0;}}</style>';
        win.document.write('<!DOCTYPE html><html><head><meta charset="UTF-8"><title>Print</title>' + css + '</head><body>' + preview.innerHTML + '</body></html>');
        win.document.close();
        setTimeout(function () { win.print(); }, 600);
    }

    function copyAsText(btn) {
        updatePreview();
        var preview = document.getElementById('mdPreview');
        if (!preview) return;
        var wrapCss = 'font-family:Segoe UI,sans-serif;color:#222;line-height:1.7;font-size:14px;';
        var innerCss = '<style>h1,h2{border-bottom:1px solid #ddd;padding-bottom:.3em;}h1{font-size:2em;}h2{font-size:1.5em;}h3{font-size:1.25em;}'
            + 'a{color:#0969da;}strong{font-weight:700;}'
            + 'code{background:#f0f0f0;padding:.2em .4em;border-radius:4px;font-family:Consolas,monospace;font-size:85%;}'
            + 'pre{background:#f6f8fa;border:1px solid #ddd;border-radius:6px;padding:16px;overflow-x:auto;font-size:13px;line-height:1.5;}'
            + 'pre code{background:transparent;padding:0;font-size:100%;}'
            + 'blockquote{border-left:3px solid #ddd;color:#666;padding:0 1em;margin:0 0 16px;}'
            + 'table{border-collapse:collapse;margin-bottom:16px;}th,td{border:1px solid #ddd;padding:6px 13px;}th{background:#f6f8fa;font-weight:600;}'
            + 'tr:nth-child(2n){background:#f8f9fa;}ul,ol{margin:0 0 16px;padding-left:2em;}li{margin-top:4px;}'
            + 'hr{border:0;height:2px;background:#ddd;margin:24px 0;}img{max-width:100%;}p{margin:0 0 16px;}</style>';
        var styledHtml = '<div style="' + wrapCss + '">' + innerCss + preview.innerHTML + '</div>';
        var plain = preview.innerText || preview.textContent || '';

        function done() {
            var label = btn.querySelector('span');
            if (!label) return;
            var orig = label.textContent;
            label.textContent = 'Copied!';
            btn.classList.add('copied');
            setTimeout(function () { label.textContent = orig; btn.classList.remove('copied'); }, 2000);
        }
        function fallback() {
            var tmp = document.createElement('div');
            tmp.setAttribute('contenteditable', 'true');
            tmp.innerHTML = styledHtml;
            tmp.style.cssText = 'position:fixed;left:-9999px;top:0;opacity:0;';
            document.body.appendChild(tmp);
            var range = document.createRange();
            range.selectNodeContents(tmp);
            var sel = window.getSelection();
            sel.removeAllRanges(); sel.addRange(range);
            try { document.execCommand('copy'); } catch (e) { /* clipboard unavailable */ }
            sel.removeAllRanges();
            document.body.removeChild(tmp);
            done();
        }
        if (navigator.clipboard && typeof ClipboardItem !== 'undefined') {
            navigator.clipboard.write([new ClipboardItem({
                'text/html': new Blob([styledHtml], { type: 'text/html' }),
                'text/plain': new Blob([plain], { type: 'text/plain' })
            })]).then(done).catch(fallback);
        } else {
            fallback();
        }
    }

    function bindClick(id, handler) {
        var el = document.getElementById(id);
        if (el) el.addEventListener('click', handler);
    }

    function initMarkdownViewer() {
        var editor = document.getElementById('mdEditor');
        if (!editor) return;
        // Re-bind per element: Blazor recreates the DOM on navigation, so a global
        // "initialized" flag would leave the new textarea unwired. Guard on the element.
        if (editor.dataset.mdInit === '1') { updatePreview(); return; }
        editor.dataset.mdInit = '1';

        var fileInput = document.getElementById('mdFileInput');
        var exportMenu = document.getElementById('mdExportMenu');
        var resizer = document.getElementById('mdResizer');

        if (!editor.value) editor.value = DEFAULT_MD;

        editor.addEventListener('input', scheduleRender);
        editor.addEventListener('keydown', function (e) {
            if (e.key !== 'Tab') return;
            e.preventDefault();
            var start = editor.selectionStart, end = editor.selectionEnd;
            editor.value = editor.value.substring(0, start) + '    ' + editor.value.substring(end);
            editor.selectionStart = editor.selectionEnd = start + 4;
            scheduleRender();
        });

        // Mode switching (editor / split / preview)
        document.querySelectorAll('.mdv-mode').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var mode = btn.getAttribute('data-mode');
                var content = document.getElementById('mdContent');
                var editorPane = document.getElementById('mdEditorPane');
                var previewPane = document.getElementById('mdPreviewPane');
                if (content) content.className = 'mdv-content mode-' + mode;
                if (editorPane) { editorPane.style.flex = ''; editorPane.style.width = ''; }
                if (previewPane) previewPane.style.flex = '';
                document.querySelectorAll('.mdv-mode').forEach(function (b) {
                    b.classList.toggle('active', b.getAttribute('data-mode') === mode);
                });
                if (mode !== 'editor') updatePreview();
            });
        });

        // Draggable resizer (split mode)
        if (resizer) {
            resizer.addEventListener('mousedown', function (e) {
                e.preventDefault();
                var content = document.getElementById('mdContent');
                var editorPane = document.getElementById('mdEditorPane');
                var previewPane = document.getElementById('mdPreviewPane');
                resizer.classList.add('dragging');
                document.body.style.cursor = 'col-resize';
                document.body.style.userSelect = 'none';
                function move(ev) {
                    if (!content || !editorPane) return;
                    var rect = content.getBoundingClientRect();
                    var pct = Math.min(Math.max(((ev.clientX - rect.left) / rect.width) * 100, 15), 85);
                    editorPane.style.flex = 'none';
                    editorPane.style.width = pct + '%';
                    if (previewPane) previewPane.style.flex = '1';
                }
                function up() {
                    resizer.classList.remove('dragging');
                    document.body.style.cursor = '';
                    document.body.style.userSelect = '';
                    document.removeEventListener('mousemove', move);
                    document.removeEventListener('mouseup', up);
                }
                document.addEventListener('mousemove', move);
                document.addEventListener('mouseup', up);
            });
        }

        // Import
        if (fileInput) {
            bindClick('mdImportBtn', function () { fileInput.click(); });
            fileInput.addEventListener('change', function () {
                var file = fileInput.files[0];
                if (!file) return;
                var reader = new FileReader();
                reader.onload = function (ev) { editor.value = ev.target.result; updatePreview(); };
                reader.readAsText(file);
                fileInput.value = '';
            });
        }

        // Export
        if (exportMenu) {
            bindClick('mdExportBtn', function (e) { e.stopPropagation(); exportMenu.classList.toggle('open'); });
            bindClick('mdExportMd', function () { mdDownload(editor.value, 'document.md', 'text/markdown'); exportMenu.classList.remove('open'); });
            bindClick('mdExportHtml', function () { exportHtml(); exportMenu.classList.remove('open'); });
            bindClick('mdExportPdf', function () { exportPdf(); exportMenu.classList.remove('open'); });
        }

        // Copy as text
        var copyBtn = document.getElementById('mdCopyBtn');
        if (copyBtn) copyBtn.addEventListener('click', function () { copyAsText(copyBtn); });

        // Close the export menu on outside click — bound once for the app lifetime.
        if (!exportMenuCloserBound) {
            exportMenuCloserBound = true;
            document.addEventListener('click', function (e) {
                var menu = document.getElementById('mdExportMenu');
                if (menu && !(e.target.closest && e.target.closest('#mdExportDropdown'))) menu.classList.remove('open');
            });
        }

        updatePreview();
    }

    window.devKitMd = window.devKitMd || {};
    window.devKitMd.initViewer = initMarkdownViewer;
})();

// DevKit — JS helpers

window.devKit = {
    copyToClipboard: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Fallback
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

    // Glass dropdown helpers
    focusEl: function (el) {
        if (el) setTimeout(() => el.focus(), 30);
    },

    // Markdown viewer helpers
    initMarkdownViewer: function () {
        var editor = document.getElementById('mdEditor');
        if (editor) {
            setTimeout(function () {
                devKit.renderMarkdown(editor.value);
            }, 100);
        }
        // File input handler
        var fi = document.getElementById('mdFileInput');
        if (fi) {
            fi.addEventListener('change', function () {
                var file = fi.files[0];
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
                fi.value = '';
            });
        }
    },

    triggerFileInput: function (id) {
        var el = document.getElementById(id);
        if (el) el.click();
    },

    renderMarkdown: function (src) {
        var el = document.getElementById('mdPreview');
        if (!el || !src) { if (el) el.innerHTML = ''; return; }

        function esc(s) { return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); }
        function inlineFmt(t) {
            var s = esc(t);
            s = s.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, '<img src="$2" alt="$1">');
            s = s.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2" target="_blank">$1</a>');
            s = s.replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>');
            s = s.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
            s = s.replace(/\*(.+?)\*/g, '<em>$1</em>');
            s = s.replace(/~~(.+?)~~/g, '<del>$1</del>');
            s = s.replace(/`([^`]+)`/g, '<code>$1</code>');
            s = s.replace(/\n/g, '<br>');
            return s;
        }

        src = src.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
        var codeBlocks = [];
        src = src.replace(/^```(\w*)\n([\s\S]*?)^```/gm, function (_, lang, code) {
            codeBlocks.push('<pre><code>' + esc(code.replace(/\n$/, '')) + '</code></pre>');
            return '\x00CB' + (codeBlocks.length - 1) + '\x00';
        });

        var lines = src.split('\n'), html = '', i = 0;
        while (i < lines.length) {
            var line = lines[i];
            if (line.trim() === '') { i++; continue; }
            var cb = line.trim().match(/^\x00CB(\d+)\x00$/);
            if (cb) { html += codeBlocks[parseInt(cb[1])]; i++; continue; }
            var hm = line.match(/^(#{1,6})\s+(.+)$/);
            if (hm) { html += '<h' + hm[1].length + '>' + inlineFmt(hm[2]) + '</h' + hm[1].length + '>'; i++; continue; }
            if (/^(\*{3,}|-{3,}|_{3,})\s*$/.test(line.trim())) { html += '<hr>'; i++; continue; }
            if (/^\|/.test(line.trim()) && i + 1 < lines.length && /^\|[\s:|-]+\|/.test(lines[i + 1].trim())) {
                var tl = []; while (i < lines.length && /^\|/.test(lines[i].trim())) { tl.push(lines[i]); i++; }
                var pr = function (l) { return l.replace(/^\|/, '').replace(/\|$/, '').split('|').map(function (c) { return c.trim(); }); };
                var hds = pr(tl[0]); var out = '<table><thead><tr>'; hds.forEach(function (h) { out += '<th>' + inlineFmt(h) + '</th>'; }); out += '</tr></thead><tbody>';
                for (var r = 2; r < tl.length; r++) { var cells = pr(tl[r]); out += '<tr>'; hds.forEach(function (_, ci) { out += '<td>' + inlineFmt(cells[ci] || '') + '</td>'; }); out += '</tr>'; }
                html += out + '</tbody></table>'; continue;
            }
            if (/^>\s?/.test(line)) { var bq = []; while (i < lines.length && /^>\s?/.test(lines[i])) { bq.push(lines[i].replace(/^>\s?/, '')); i++; } html += '<blockquote>' + bq.map(inlineFmt).join('<br>') + '</blockquote>'; continue; }
            if (/^[\s]*[-*+]\s/.test(line)) { html += '<ul>'; while (i < lines.length && /^[\s]*[-*+]\s/.test(lines[i])) { var it = lines[i].replace(/^[\s]*[-*+]\s/, ''); if (/^\[x\]/i.test(it)) html += '<li style="list-style:none;margin-left:-1.5em;"><input type="checkbox" checked disabled> ' + inlineFmt(it.substring(3).trim()) + '</li>'; else if (/^\[ \]/.test(it)) html += '<li style="list-style:none;margin-left:-1.5em;"><input type="checkbox" disabled> ' + inlineFmt(it.substring(3).trim()) + '</li>'; else html += '<li>' + inlineFmt(it) + '</li>'; i++; } html += '</ul>'; continue; }
            if (/^[\s]*\d+\.\s/.test(line)) { html += '<ol>'; while (i < lines.length && /^[\s]*\d+\.\s/.test(lines[i])) { html += '<li>' + inlineFmt(lines[i].replace(/^[\s]*\d+\.\s/, '')) + '</li>'; i++; } html += '</ol>'; continue; }
            var pL = []; while (i < lines.length && lines[i].trim() !== '' && !/^#{1,6}\s/.test(lines[i]) && !/^(\*{3,}|-{3,}|_{3,})\s*$/.test(lines[i].trim()) && !/^\|/.test(lines[i].trim()) && !/^>\s?/.test(lines[i]) && !/^[\s]*[-*+]\s/.test(lines[i]) && !/^[\s]*\d+\.\s/.test(lines[i]) && !/^\x00CB/.test(lines[i].trim())) { pL.push(lines[i]); i++; }
            if (pL.length) html += '<p>' + inlineFmt(pL.join('\n')) + '</p>';
        }
        html = html.replace(/\x00CB(\d+)\x00/g, function (_, idx) { return codeBlocks[parseInt(idx)]; });
        el.innerHTML = html;
    },

    _clickOutsideHandlers: new Map(),

    registerClickOutside: function (el, dotNetRef) {
        const handler = (e) => {
            if (el && !el.contains(e.target)) {
                dotNetRef.invokeMethodAsync('CloseDropdown');
            }
        };
        this._clickOutsideHandlers.set(el, handler);
        setTimeout(() => document.addEventListener('click', handler), 10);
    },

    unregisterClickOutside: function (el) {
        const handler = this._clickOutsideHandlers.get(el);
        if (handler) {
            document.removeEventListener('click', handler);
            this._clickOutsideHandlers.delete(el);
        }
    }
};

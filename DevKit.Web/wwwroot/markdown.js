// DevKit — Markdown rendering: the syntax highlighter and the parser behind it.
// Pairs with markdown-viewer.js, which drives the page.

(function () {
    'use strict';

    // ─── Shared helpers ───

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

    // ════════════════════════════════
    //  SYNTAX HIGHLIGHTER
    // ════════════════════════════════
    function highlightCode(code, lang) {
        var h = escapeHtml(code);
        lang = (lang || '').toLowerCase();
        var keywords, types, commentLine, commentBlock, strChars;

        if (['js', 'javascript', 'typescript', 'ts'].indexOf(lang) >= 0) {
            keywords = 'var|let|const|function|return|if|else|for|while|do|switch|case|break|continue|new|this|class|extends|import|export|from|default|try|catch|finally|throw|typeof|instanceof|in|of|async|await|yield|null|undefined|true|false|void|delete|super|static|get|set|=>';
            types = 'Array|Object|String|Number|Boolean|Promise|Map|Set|RegExp|Error|Date|Math|JSON|console|window|document';
            commentLine = '//'; commentBlock = ['/\\*', '\\*/']; strChars = '\'"`';
        } else if (['py', 'python'].indexOf(lang) >= 0) {
            keywords = 'def|class|return|if|elif|else|for|while|break|continue|import|from|as|try|except|finally|raise|with|yield|lambda|pass|del|global|nonlocal|assert|True|False|None|and|or|not|is|in';
            types = 'int|float|str|bool|list|dict|tuple|set|range|print|len|type|super|self|open|map|filter|zip|enumerate|sorted|reversed|input|isinstance';
            commentLine = '#'; commentBlock = null; strChars = '\'"`';
        } else if (['java', 'c', 'cpp', 'csharp', 'cs', 'c\\+\\+'].indexOf(lang) >= 0) {
            keywords = 'public|private|protected|static|final|void|int|char|float|double|long|short|boolean|byte|class|interface|extends|implements|new|return|if|else|for|while|do|switch|case|break|continue|try|catch|finally|throw|throws|import|package|null|true|false|this|super|abstract|synchronized|volatile|transient|const|enum|struct|typedef|sizeof|unsigned|signed|auto|register|extern|inline|virtual|override|namespace|using|template|typename';
            types = 'String|Integer|Boolean|Long|Double|Float|List|ArrayList|Map|HashMap|Set|HashSet|System|Math|Arrays|Collections|Object|Exception|Thread|Runnable|Optional|Stream|Console|Scanner|File';
            commentLine = '//'; commentBlock = ['/\\*', '\\*/']; strChars = '\'"`';
        } else if (['html', 'xml', 'svg'].indexOf(lang) >= 0) {
            h = h.replace(/(&lt;\/?)([\w-]+)/g, '$1<span class="tag">$2</span>');
            h = h.replace(/([\w-]+)(=)(&quot;[^&]*&quot;)/g, '<span class="attr">$1</span>$2<span class="val">$3</span>');
            h = h.replace(/(&lt;!--[\s\S]*?--&gt;)/g, '<span class="cm">$1</span>');
            return h;
        } else if (['css', 'scss', 'less'].indexOf(lang) >= 0) {
            keywords = 'import|media|keyframes|font-face|charset|supports|page';
            types = 'px|em|rem|vh|vw|deg|fr|auto|none|inherit|initial|unset|flex|grid|block|inline|relative|absolute|fixed|sticky';
            commentLine = null; commentBlock = ['/\\*', '\\*/']; strChars = '\'"`';
        } else if (['bash', 'sh', 'shell', 'zsh', 'powershell', 'ps1'].indexOf(lang) >= 0) {
            keywords = 'if|then|else|elif|fi|for|while|do|done|case|esac|in|function|return|exit|local|export|source|alias|unset|readonly|shift|break|continue|cd|echo|printf|read|test|param|begin|process|end';
            types = 'grep|sed|awk|find|ls|cat|mkdir|rm|cp|mv|chmod|chown|curl|wget|git|npm|node|python|pip|docker|sudo|apt|yum|brew|dotnet|Get-ChildItem|Select-Object|Where-Object';
            commentLine = '#'; commentBlock = null; strChars = '\'"`';
        } else if (['json'].indexOf(lang) >= 0) {
            h = h.replace(/(&quot;[^&]*?&quot;)\s*:/g, '<span class="attr">$1</span>:');
            h = h.replace(/:(\s*)(&quot;[^&]*?&quot;)/g, ':$1<span class="str">$2</span>');
            h = h.replace(/:\s*(true|false|null)\b/g, ': <span class="kw">$1</span>');
            h = h.replace(/:\s*(\d[\d.]*)/g, ': <span class="num">$1</span>');
            return h;
        } else if (['sql'].indexOf(lang) >= 0) {
            keywords = 'SELECT|FROM|WHERE|AND|OR|NOT|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|ALTER|DROP|INDEX|JOIN|LEFT|RIGHT|INNER|OUTER|ON|AS|ORDER|BY|GROUP|HAVING|LIMIT|OFFSET|UNION|ALL|DISTINCT|COUNT|SUM|AVG|MIN|MAX|BETWEEN|LIKE|IN|IS|NULL|EXISTS|CASE|WHEN|THEN|ELSE|END|PRIMARY|KEY|FOREIGN|REFERENCES|UNIQUE|DEFAULT|CHECK|CONSTRAINT';
            types = 'INT|VARCHAR|TEXT|BOOLEAN|DATE|TIMESTAMP|FLOAT|DOUBLE|DECIMAL|CHAR|BLOB|SERIAL|BIGINT|SMALLINT';
            commentLine = '--'; commentBlock = ['/\\*', '\\*/']; strChars = '\'';
        } else {
            keywords = 'var|let|const|function|return|if|else|for|while|class|def|import|from|export|true|false|null|none|nil|void|int|string|bool|float|double|public|private|static|new|this|self|try|catch|throw|break|continue|switch|case|default';
            types = 'print|println|console|log|Math|Array|Object|String|List|Map|Set';
            commentLine = '//'; commentBlock = ['/\\*', '\\*/']; strChars = '\'"`';
        }

        var i = 0, chars = h, out = '';
        while (i < chars.length) {
            var rest = chars.substring(i);
            var matched = false;

            if (commentBlock) {
                var bStart = new RegExp('^' + commentBlock[0]);
                var bEnd = new RegExp(commentBlock[1]);
                if (bStart.test(rest)) {
                    var endMatch = rest.substring(2).search(bEnd);
                    var endPos = endMatch >= 0 ? endMatch + 2 + commentBlock[1].replace(/\\/g, '').length : rest.length;
                    out += '<span class="cm">' + rest.substring(0, endPos) + '</span>';
                    i += endPos; matched = true;
                }
            }
            if (!matched && commentLine) {
                var clEsc = commentLine.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
                var clM = rest.match(new RegExp('^' + clEsc + '.*'));
                if (clM) { out += '<span class="cm">' + clM[0] + '</span>'; i += clM[0].length; matched = true; }
            }
            if (!matched && strChars) {
                for (var si = 0; si < strChars.length; si++) {
                    var sc = strChars[si];
                    var actualStart = sc === '"' ? '&quot;' : sc;
                    if (rest.substring(0, actualStart.length) === actualStart) {
                        var endIdx = -1;
                        for (var j = actualStart.length; j < rest.length; j++) {
                            if (rest.charAt(j) === '\\') { j++; continue; }
                            if (rest.substring(j, j + actualStart.length) === actualStart) { endIdx = j + actualStart.length; break; }
                        }
                        if (endIdx < 0) endIdx = rest.length;
                        out += '<span class="str">' + rest.substring(0, endIdx) + '</span>';
                        i += endIdx; matched = true; break;
                    }
                }
            }
            if (!matched) { out += rest.charAt(0); i++; }
        }
        h = out;

        if (keywords) h = h.replace(new RegExp('\\b(' + keywords + ')\\b', 'g'), function (m) { return '<span class="kw">' + m + '</span>'; });
        if (types) h = h.replace(new RegExp('\\b(' + types + ')\\b', 'g'), function (m) { return '<span class="type">' + m + '</span>'; });
        h = h.replace(/\b(\d+\.?\d*)\b/g, '<span class="num">$1</span>');
        h = h.replace(/\b([a-zA-Z_]\w*)\s*\(/g, '<span class="fn">$1</span>(');
        return h;
    }

    // ════════════════════════════════
    //  MARKDOWN PARSER
    // ════════════════════════════════
    function inlineFormat(text) {
        var parts = text.split(/(\x00(?:INLINE|MATHINLINE|CODEBLOCK|MATHBLOCK)\d+\x00)/);
        var s = parts.map(function (part) { return /^\x00/.test(part) ? part : escapeHtml(part); }).join('');

        s = s.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, function (_, alt, url) {
            var safe = safeImageUrl(url);
            return safe ? '<img src="' + safe + '" alt="' + alt + '">' : alt;
        });
        s = s.replace(/\[([^\]]+)\]\(([^)]+)\)/g, function (_, label, url) {
            var safe = safeLinkUrl(url);
            return safe ? '<a href="' + safe + '" target="_blank" rel="noopener noreferrer">' + label + '</a>' : label;
        });
        s = s.replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>');
        s = s.replace(/___(.+?)___/g, '<strong><em>$1</em></strong>');
        s = s.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
        s = s.replace(/__(.+?)__/g, '<strong>$1</strong>');
        s = s.replace(/\*(.+?)\*/g, '<em>$1</em>');
        s = s.replace(/(?<!\w)_(.+?)_(?!\w)/g, '<em>$1</em>');
        s = s.replace(/~~(.+?)~~/g, '<del>$1</del>');
        s = s.replace(/\n/g, '<br>');
        return s;
    }

    function parseTable(tableLines) {
        if (tableLines.length < 2) return '';
        var parseRow = function (line) {
            return line.replace(/^\|/, '').replace(/\|$/, '').split('|').map(function (c) { return c.trim(); });
        };
        var headers = parseRow(tableLines[0]);
        var out = '<table><thead><tr>';
        headers.forEach(function (hd) { out += '<th>' + inlineFormat(hd) + '</th>'; });
        out += '</tr></thead><tbody>';
        for (var r = 2; r < tableLines.length; r++) {
            var cells = parseRow(tableLines[r]);
            out += '<tr>';
            headers.forEach(function (_, ci) { out += '<td>' + inlineFormat(cells[ci] || '') + '</td>'; });
            out += '</tr>';
        }
        return out + '</tbody></table>';
    }

    function parseMarkdown(src) {
        src = src.replace(/\r\n/g, '\n').replace(/\r/g, '\n');

        var codeBlocks = [];
        src = src.replace(/^```(\w*)\n([\s\S]*?)^```/gm, function (_, lang, code) {
            codeBlocks.push('<pre><code>' + highlightCode(code.replace(/\n$/, ''), lang) + '</code></pre>');
            return '\x00CODEBLOCK' + (codeBlocks.length - 1) + '\x00';
        });
        var inlineCodes = [];
        src = src.replace(/`([^`\n]+?)`/g, function (_, code) {
            inlineCodes.push('<code>' + escapeHtml(code) + '</code>');
            return '\x00INLINE' + (inlineCodes.length - 1) + '\x00';
        });
        var mathBlocks = [];
        src = src.replace(/\$\$([\s\S]+?)\$\$/g, function (_, math) {
            mathBlocks.push('<div class="math-block">' + escapeHtml(math.trim()) + '</div>');
            return '\x00MATHBLOCK' + (mathBlocks.length - 1) + '\x00';
        });
        var mathInlines = [];
        src = src.replace(/\$([^\n$]+?)\$/g, function (_, math) {
            mathInlines.push('<span class="math-inline">' + escapeHtml(math.trim()) + '</span>');
            return '\x00MATHINLINE' + (mathInlines.length - 1) + '\x00';
        });

        var lines = src.split('\n'), html = '', i = 0;
        while (i < lines.length) {
            var line = lines[i];
            if (line.trim() === '') { i++; continue; }

            var cb = line.trim().match(/^\x00CODEBLOCK(\d+)\x00$/);
            if (cb) { html += codeBlocks[parseInt(cb[1], 10)]; i++; continue; }
            var mb = line.trim().match(/^\x00MATHBLOCK(\d+)\x00$/);
            if (mb) { html += mathBlocks[parseInt(mb[1], 10)]; i++; continue; }

            var hm = line.match(/^(#{1,6})\s+(.+)$/);
            if (hm) { html += '<h' + hm[1].length + '>' + inlineFormat(hm[2]) + '</h' + hm[1].length + '>'; i++; continue; }

            if (/^(\*{3,}|-{3,}|_{3,})\s*$/.test(line.trim())) { html += '<hr>'; i++; continue; }

            if (/^\|/.test(line.trim()) && i + 1 < lines.length && /^\|[\s:|-]+\|/.test(lines[i + 1].trim())) {
                var tbl = [];
                while (i < lines.length && /^\|/.test(lines[i].trim())) { tbl.push(lines[i]); i++; }
                html += parseTable(tbl); continue;
            }

            if (/^>\s?/.test(line)) {
                var bq = [];
                while (i < lines.length && (/^>\s?/.test(lines[i]) || (lines[i].trim() !== '' && bq.length > 0 && !/^[#|\-\*\d]/.test(lines[i])))) {
                    bq.push(lines[i].replace(/^>\s?/, '')); i++;
                }
                html += '<blockquote>' + parseMarkdown(bq.join('\n')) + '</blockquote>'; continue;
            }

            if (/^[\s]*[-*+]\s/.test(line)) {
                var ul = [];
                while (i < lines.length && /^[\s]*[-*+]\s/.test(lines[i])) { ul.push(lines[i].replace(/^[\s]*[-*+]\s/, '')); i++; }
                html += '<ul>';
                ul.forEach(function (item) {
                    if (/^\[x\]/i.test(item)) html += '<li class="task-li"><input type="checkbox" checked disabled> ' + inlineFormat(item.substring(3).trim()) + '</li>';
                    else if (/^\[ \]/.test(item)) html += '<li class="task-li"><input type="checkbox" disabled> ' + inlineFormat(item.substring(3).trim()) + '</li>';
                    else html += '<li>' + inlineFormat(item) + '</li>';
                });
                html += '</ul>'; continue;
            }

            if (/^[\s]*\d+\.\s/.test(line)) {
                var ol = [];
                while (i < lines.length && /^[\s]*\d+\.\s/.test(lines[i])) { ol.push(lines[i].replace(/^[\s]*\d+\.\s/, '')); i++; }
                html += '<ol>';
                ol.forEach(function (item) { html += '<li>' + inlineFormat(item) + '</li>'; });
                html += '</ol>'; continue;
            }

            var pL = [];
            while (i < lines.length && lines[i].trim() !== '' &&
                !/^#{1,6}\s/.test(lines[i]) && !/^(\*{3,}|-{3,}|_{3,})\s*$/.test(lines[i].trim()) &&
                !/^\|/.test(lines[i].trim()) && !/^>\s?/.test(lines[i]) &&
                !/^[\s]*[-*+]\s/.test(lines[i]) && !/^[\s]*\d+\.\s/.test(lines[i]) &&
                !/^\x00CODEBLOCK/.test(lines[i].trim()) && !/^\x00MATHBLOCK/.test(lines[i].trim())) {
                pL.push(lines[i]); i++;
            }
            if (pL.length) html += '<p>' + inlineFormat(pL.join('\n')) + '</p>';
        }

        html = html.replace(/\x00CODEBLOCK(\d+)\x00/g, function (_, idx) { return codeBlocks[parseInt(idx, 10)]; });
        html = html.replace(/\x00MATHBLOCK(\d+)\x00/g, function (_, idx) { return mathBlocks[parseInt(idx, 10)]; });
        html = html.replace(/\x00MATHINLINE(\d+)\x00/g, function (_, idx) { return mathInlines[parseInt(idx, 10)]; });
        html = html.replace(/\x00INLINE(\d+)\x00/g, function (_, idx) { return inlineCodes[parseInt(idx, 10)]; });
        return html;
    }

    window.devKitMd = window.devKitMd || {};
    window.devKitMd.render = parseMarkdown;
})();

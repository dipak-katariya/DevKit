// DevKit — the JS interop surface Blazor calls into (window.devKit).

(function () {
    'use strict';

    var FOCUS_DELAY_MS = 30;
    var CLICK_BIND_DELAY_MS = 10;

    // The parser, and the escaping it applies to every link, image and span of text, live in
    // markdown.js. This file never builds markup of its own.
    function parseMarkdown(src) { return window.devKitMd.render(src); }

    // ─── Public API ───

    var clickOutsideHandlers = new Map();
    var commandKeyBound = false;

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

        triggerFileInput: function (id) {
            var el = document.getElementById(id);
            if (el) el.click();
        },

        historyBack: function () { window.history.back(); },
        historyForward: function () { window.history.forward(); },

        focusById: function (id) {
            var el = document.getElementById(id);
            if (el) el.focus();
        },

        registerCommandKey: function (id) {
            if (commandKeyBound) return;
            commandKeyBound = true;
            document.addEventListener('keydown', function (e) {
                if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
                    var el = document.getElementById(id);
                    if (el) { e.preventDefault(); el.focus(); }
                }
            });
        },

        initMarkdownViewer: function () { window.devKitMd.initViewer(); },
        renderMarkdown: function (src) {
            var el = document.getElementById('mdPreview');
            if (el) el.innerHTML = src ? parseMarkdown(src) : '';
        },

        registerClickOutside: function (el, dotNetRef) {
            const handler = (e) => {
                // A component removed while open could not unregister (its element was already gone),
                // so its listener drops itself instead of calling a disposed .NET reference.
                if (el && !el.isConnected) {
                    document.removeEventListener('click', handler);
                    clickOutsideHandlers.delete(el);
                    return;
                }
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

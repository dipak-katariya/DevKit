// DevKit: the JS interop surface Blazor calls into (window.devKit).

(function () {
    'use strict';

    var FOCUS_DELAY_MS = 30;
    var CLICK_BIND_DELAY_MS = 10;
    var VAULT_ADDRESS = '/devkit-vault';
    var HTTP_NO_CONTENT = 204;

    // The parser, and the escaping it applies to every link, image and span of text, live in
    // markdown.js. This file never builds markup of its own.
    function parseMarkdown(src) { return window.devKitMd.render(src); }

    // One entry per element, { handler, timer }. The timer is the pending bind, kept so that unregistering
    // before it fires cannot leave a listener behind.
    var clickOutsideHandlers = new Map();

    function unregisterClickOutside(el) {
        var entry = clickOutsideHandlers.get(el);
        if (!entry) {
            return;
        }
        clearTimeout(entry.timer);
        document.removeEventListener('click', entry.handler);
        clickOutsideHandlers.delete(el);
    }

    function registerClickOutside(el, dotNetRef) {
        if (!el) {
            return;
        }
        // Registering again replaces the earlier listener, so an element never has two.
        unregisterClickOutside(el);
        var entry = { handler: null, timer: 0 };
        entry.handler = function (e) {
            if (el.isConnected && el.contains(e.target)) {
                return;
            }
            // One-shot: an outside click always ends the registration, and the component registers again
            // the next time its panel opens. An element already gone from the page only drops its listener,
            // because its component, and the .NET reference with it, has been disposed.
            unregisterClickOutside(el);
            if (el.isConnected) {
                dotNetRef.invokeMethodAsync('CloseDropdown');
            }
        };
        // Bound a moment later, so the click that opened the panel does not reach the new listener.
        entry.timer = setTimeout(function () {
            document.addEventListener('click', entry.handler);
        }, CLICK_BIND_DELAY_MS);
        clickOutsideHandlers.set(el, entry);
    }

    // ─── Public API ───

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

        // Shared mode keeps the person's settings in this browser's cookies, sealed by DevKit (ClientVault.cs). The
        // cookies are HttpOnly, so this page only carries the sealed text between DevKit and the server, and cannot
        // open it. A thrown error reaches DevKit as the reason the settings were not read or kept.
        loadVault: async function () {
            var response = await fetch(VAULT_ADDRESS, { credentials: 'same-origin', cache: 'no-store' });
            if (response.status === HTTP_NO_CONTENT) {
                return null;
            }
            if (!response.ok) {
                throw new Error('The settings kept in this browser could not be read (HTTP ' + response.status + ').');
            }
            return await response.text();
        },
        saveVault: async function (sealed) {
            var response = await fetch(VAULT_ADDRESS, {
                method: 'POST',
                credentials: 'same-origin',
                cache: 'no-store',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ sealed: sealed })
            });
            if (!response.ok) {
                throw new Error('This browser did not keep the settings (HTTP ' + response.status + ').');
            }
        },

        registerClickOutside: registerClickOutside,
        unregisterClickOutside: unregisterClickOutside
    };
})();

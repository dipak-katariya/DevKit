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
        document.removeEventListener('focusin', entry.handler);
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
            // One-shot: an outside click, or focus moving outside (Tab past the panel), always ends the
            // registration, and the component registers again the next time its panel opens. An element already
            // gone from the page only drops its listener, because its component, and the .NET reference with it,
            // has been disposed.
            unregisterClickOutside(el);
            if (el.isConnected) {
                dotNetRef.invokeMethodAsync('CloseDropdown');
            }
        };
        // Bound a moment later, so the click that opened the panel does not reach the new listener.
        entry.timer = setTimeout(function () {
            document.addEventListener('click', entry.handler);
            document.addEventListener('focusin', entry.handler);
        }, CLICK_BIND_DELAY_MS);
        clickOutsideHandlers.set(el, entry);
    }

    // The phone menu's drawer: MainLayout is told when the window grows wide enough for the sidebar to be back
    // in the page, so it closes a drawer left open. One layout per page, so one watch.
    var wideWatch = null;

    function unwatchWideScreen() {
        if (!wideWatch) {
            return;
        }
        wideWatch.query.removeEventListener('change', wideWatch.handler);
        wideWatch = null;
    }

    function watchWideScreen(media, dotNetRef) {
        unwatchWideScreen();
        var query = window.matchMedia(media);
        var handler = function (e) {
            if (e.matches) {
                dotNetRef.invokeMethodAsync('CloseNavForWideScreen');
            }
        };
        query.addEventListener('change', handler);
        wideWatch = { query: query, handler: handler };
    }

    // A list that is one Tab stop (data-roving): the arrow keys, Home and End move focus between its items
    // (data-roving-item). Handled here rather than in .NET so the page does not scroll as the arrows move and no key
    // waits on the server. A list runs down (Up and Down); data-roving="row" runs across (Left and Right, the tabs);
    // data-roving="grid" is a table whose items move along a row with Left and Right and between rows with Up and
    // Down. Either the component keeps the item that last had focus as the one Tab reaches, or the group says
    // data-roving-stop and this file keeps it.
    var ROVING_STEPS = { ArrowDown: 1, ArrowUp: -1 };
    var ROW_STEPS = { ArrowRight: 1, ArrowLeft: -1 };

    function rovingItems(container) {
        return Array.prototype.slice.call(container.querySelectorAll('[data-roving-item]'));
    }

    function stepWithin(items, item, key, steps) {
        if (key === 'Home') {
            return items[0];
        }
        if (key === 'End') {
            return items[items.length - 1];
        }
        if (!steps[key]) {
            return null;
        }
        var at = items.indexOf(item);
        return items[Math.max(0, Math.min(items.length - 1, at + steps[key]))];
    }

    // Up and Down go to the nearest row that has items, to the item in the same column, or the closest one before it.
    function gridTarget(group, item, key) {
        var row = item.closest('tr');
        if (!ROVING_STEPS[key]) {
            return stepWithin(rovingItems(row), item, key, ROW_STEPS);
        }
        var rows = Array.prototype.slice.call(group.querySelectorAll('tr')).filter(function (tr) {
            return tr.querySelector('[data-roving-item]');
        });
        var to = rows[rows.indexOf(row) + ROVING_STEPS[key]];
        if (!to) {
            return item;
        }
        var column = item.closest('td').cellIndex;
        var candidates = rovingItems(to);
        var best = candidates[0];
        candidates.forEach(function (c) {
            if (c.closest('td').cellIndex <= column) {
                best = c;
            }
        });
        return best;
    }

    document.addEventListener('keydown', function (e) {
        var item = e.target.closest ? e.target.closest('[data-roving-item]') : null;
        var group = item ? item.closest('[data-roving]') : null;
        if (!group || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) {
            return;
        }
        var kind = group.getAttribute('data-roving');
        var next = kind === 'grid'
            ? gridTarget(group, item, e.key)
            : stepWithin(rovingItems(group), item, e.key, kind === 'row' ? ROW_STEPS : ROVING_STEPS);
        if (!next) {
            return;
        }
        e.preventDefault();
        next.focus();
    });

    document.addEventListener('focusin', function (e) {
        var item = e.target.closest ? e.target.closest('[data-roving-item]') : null;
        var group = item ? item.closest('[data-roving-stop]') : null;
        if (!group) {
            return;
        }
        rovingItems(group).forEach(function (other) {
            other.tabIndex = other === item ? 0 : -1;
        });
    });

    // Open dialogs, newest last, each with what had focus before it, so closing hands focus back there. The dialog
    // itself takes focus first, so a screen reader reads its title before its contents; Tab then stays inside the
    // newest dialog, wrapping at either end.
    var dialogs = [];
    var FOCUSABLE = 'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), ' +
        'textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

    function focusablesIn(el) {
        return Array.prototype.slice.call(el.querySelectorAll(FOCUSABLE)).filter(function (x) {
            return x.getClientRects().length > 0;
        });
    }

    function openDialog(el, key) {
        if (!el) {
            return;
        }
        dialogs.push({ el: el, key: key, opener: document.activeElement });
        el.focus();
    }

    function closeDialog(key) {
        var at = dialogs.findIndex(function (d) { return d.key === key; });
        if (at < 0) {
            return;
        }
        var opener = dialogs.splice(at, 1)[0].opener;
        if (opener && opener.isConnected) {
            opener.focus();
        }
    }

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Tab' || !dialogs.length) {
            return;
        }
        var el = dialogs[dialogs.length - 1].el;
        var items = focusablesIn(el);
        var active = document.activeElement;
        var outside = active === el || !el.contains(active);
        if (!items.length) {
            e.preventDefault();
            return;
        }
        if (e.shiftKey && (outside || active === items[0])) {
            e.preventDefault();
            items[items.length - 1].focus();
        } else if (!e.shiftKey && (outside || active === items[items.length - 1])) {
            e.preventDefault();
            items[0].focus();
        }
    });

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

        // The first control in a popover is its trigger.
        focusFirst: function (el) {
            var target = el && el.querySelector('button:not([disabled]), [href], input:not([disabled]), [tabindex]:not([tabindex="-1"])');
            if (target) target.focus();
        },

        // Focus stays in a dropdown's search box while the arrow keys move, so the list does not scroll by itself.
        revealEl: function (id) {
            var el = document.getElementById(id);
            if (el) el.scrollIntoView({ block: 'nearest' });
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
                    if (el) {
                        e.preventDefault();
                        // A box that already has focus (results closed with Escape) gets it again, so its focus
                        // handler reopens the results.
                        if (document.activeElement === el) { el.blur(); }
                        el.focus();
                    }
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
        unregisterClickOutside: unregisterClickOutside,
        openDialog: openDialog,
        closeDialog: closeDialog,
        watchWideScreen: watchWideScreen,
        unwatchWideScreen: unwatchWideScreen
    };
})();

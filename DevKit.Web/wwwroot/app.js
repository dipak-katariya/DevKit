// DevKit: the JS interop surface Blazor calls into (window.devKit).

(function () {
    'use strict';

    var FOCUS_DELAY_MS = 30;
    var CLICK_BIND_DELAY_MS = 10;

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

    // The first-visit tour (GuideTour.razor): a hole in a dimmed screen over the part a step talks about, and the
    // step's note beside it. The note goes right of the part, else below, left or above, whichever fits first; with
    // no part on screen it sits on its own. Resizing or scrolling places both again.
    var TOUR_PAD = 6;
    var TOUR_GAP = 14;
    var TOUR_MARGIN = 16;
    var TOUR_PHONE = 600;
    var tourState = null;
    var tourFrame = 0;

    function tourTargetRect(selector) {
        var el = document.querySelector(selector);
        if (!el) {
            return null;
        }
        var r = el.getBoundingClientRect();
        var onScreen = r.width >= 1 && r.height >= 1 && r.right > 0 && r.bottom > 0 &&
            r.left < window.innerWidth && r.top < window.innerHeight;
        return onScreen ? r : null;
    }

    function tourSpot(rect, cw, ch) {
        var w = window.innerWidth, h = window.innerHeight;
        var spots = [
            { x: rect.right + TOUR_PAD + TOUR_GAP, y: rect.top - TOUR_PAD, fits: function (s) { return s.x + cw <= w - TOUR_MARGIN; } },
            { x: rect.left - TOUR_PAD, y: rect.bottom + TOUR_PAD + TOUR_GAP, fits: function (s) { return s.y + ch <= h - TOUR_MARGIN; } },
            { x: rect.left - TOUR_PAD - TOUR_GAP - cw, y: rect.top - TOUR_PAD, fits: function (s) { return s.x >= TOUR_MARGIN; } },
            { x: rect.left - TOUR_PAD, y: rect.top - TOUR_PAD - TOUR_GAP - ch, fits: function (s) { return s.y >= TOUR_MARGIN; } }
        ];
        var spot = spots.find(function (s) { return s.fits(s); }) || spots[1];
        return {
            x: Math.min(Math.max(spot.x, TOUR_MARGIN), w - TOUR_MARGIN - cw),
            y: Math.min(Math.max(spot.y, TOUR_MARGIN), h - TOUR_MARGIN - ch)
        };
    }

    // With nothing to point at, the note sits in the middle, or at the bottom of a phone where a thumb reaches it,
    // and the hole shrinks to a point there, so the next step's hole grows out of the note.
    function tourFreeSpot(cw, ch) {
        var w = window.innerWidth, h = window.innerHeight;
        var x = (w - cw) / 2;
        var y = w <= TOUR_PHONE ? h - TOUR_MARGIN - ch : (h - ch) / 2;
        return { x: x, y: y, hole: { left: w / 2, top: y + ch / 2, width: 0, height: 0 } };
    }

    // Placed with transforms, so moving from one step to the next is a glide (guide.css) rather than a jump.
    function placeTour() {
        tourFrame = 0;
        var card = tourState && tourState.card;
        var root = card && card.closest('.tour');
        if (!root) {
            return;
        }
        var rect = null;
        for (var i = 0; i < tourState.targets.length && !rect; i++) {
            rect = tourTargetRect(tourState.targets[i]);
        }
        root.classList.toggle('tour-free', !rect);
        var cw = card.offsetWidth, ch = card.offsetHeight;
        var spot, hole;
        if (rect) {
            spot = tourSpot(rect, cw, ch);
            hole = { left: rect.left - TOUR_PAD, top: rect.top - TOUR_PAD, width: rect.width + TOUR_PAD * 2, height: rect.height + TOUR_PAD * 2 };
        } else {
            spot = tourFreeSpot(cw, ch);
            hole = spot.hole;
        }
        var holeEl = root.querySelector('.tour-hole');
        holeEl.style.transform = 'translate(' + hole.left + 'px,' + hole.top + 'px)';
        holeEl.style.width = hole.width + 'px';
        holeEl.style.height = hole.height + 'px';
        card.style.transform = 'translate(' + Math.round(spot.x) + 'px,' + Math.round(spot.y) + 'px)';
        if (!root.classList.contains('tour-ready')) {
            // The first place is where the tour opens; only the moves after it glide.
            requestAnimationFrame(function () { root.classList.add('tour-ready'); });
        }
    }

    // Resizing and scrolling follow the page at once: a glide there would lag behind it.
    function queueTourPlace() {
        if (tourFrame) {
            return;
        }
        var root = tourState && tourState.card && tourState.card.closest('.tour');
        if (root) {
            root.classList.remove('tour-ready');
        }
        tourFrame = requestAnimationFrame(placeTour);
    }

    function tourPlace(card, targets) {
        if (!tourState) {
            window.addEventListener('resize', queueTourPlace);
            window.addEventListener('scroll', queueTourPlace, true);
        }
        tourState = { card: card, targets: targets || [] };
        placeTour();
    }

    function tourEnd() {
        tourState = null;
        if (tourFrame) {
            cancelAnimationFrame(tourFrame);
            tourFrame = 0;
        }
        window.removeEventListener('resize', queueTourPlace);
        window.removeEventListener('scroll', queueTourPlace, true);
    }

    // The Guide's contents (Guide.razor): the link of the section being read is marked, and on a phone, where the
    // contents are one row, that link is scrolled into the row. A section counts as being read while it crosses a band
    // near the top of the page; of several, the first wins.
    var tocObserver = null;
    var TOC_BAND = '-15% 0px -70% 0px';
    var TOC_BAND_TOP = 0.15;

    function prefersStill() {
        return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    }

    function markTocLink(toc, links, id) {
        if (!id && toc.scrollWidth > toc.clientWidth) {
            toc.scrollTo({ left: 0, behavior: prefersStill() ? 'auto' : 'smooth' });
        }
        links.forEach(function (a) {
            var on = a.hash === '#' + id;
            a.classList.toggle('on', on);
            if (on) {
                a.setAttribute('aria-current', 'location');
            } else {
                a.removeAttribute('aria-current');
            }
            if (on && toc.scrollWidth > toc.clientWidth) {
                toc.scrollTo({ left: a.offsetLeft - TOUR_MARGIN, behavior: prefersStill() ? 'auto' : 'smooth' });
            }
        });
    }

    // Blazor follows a "#section" link with a jump; within the Guide the link glides there instead, so the reader
    // sees where it went. The address still gets the section, as a jump would give it.
    var tocClicks = null;

    function glideToSection(e) {
        var a = e.target.closest ? e.target.closest('a[href*="#"]') : null;
        var section = a && a.origin === location.origin && a.pathname === location.pathname
            ? document.getElementById(a.hash.slice(1)) : null;
        if (!section || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) {
            return;
        }
        e.preventDefault();
        e.stopPropagation();
        history.replaceState(history.state, '', a.hash);
        section.scrollIntoView({ behavior: prefersStill() ? 'auto' : 'smooth', block: 'start' });
    }

    function lastSectionAbove(sections, toc) {
        var shell = toc.closest('.shell');
        var box = shell ? shell.getBoundingClientRect() : { top: 0, height: window.innerHeight };
        var line = box.top + box.height * TOC_BAND_TOP;
        var above = sections.filter(function (s) { return s.getBoundingClientRect().top <= line; });
        return above.length ? above[above.length - 1] : null;
    }

    function watchToc(toc) {
        unwatchToc();
        var guide = toc && toc.closest('.guide');
        if (guide) {
            guide.addEventListener('click', glideToSection);
            tocClicks = guide;
        }
        if (!toc || !('IntersectionObserver' in window)) {
            return;
        }
        var links = Array.prototype.slice.call(toc.querySelectorAll('a[href*="#"]'));
        var sections = links.map(function (a) { return document.getElementById(a.hash.slice(1)); }).filter(Boolean);
        var reading = new Set();
        var current = null;
        tocObserver = new IntersectionObserver(function (entries) {
            entries.forEach(function (e) {
                if (e.isIntersecting) {
                    reading.add(e.target);
                } else {
                    reading.delete(e.target);
                }
            });
            // Between two sections, the one above is still the one being read; above the first, none is.
            var first = sections.find(function (s) { return reading.has(s); }) || lastSectionAbove(sections, toc);
            var id = first ? first.id : null;
            if (id !== current) {
                current = id;
                markTocLink(toc, links, current);
            }
        }, { root: toc.closest('.shell'), rootMargin: TOC_BAND });
        sections.forEach(function (s) { tocObserver.observe(s); });
    }

    function unwatchToc() {
        if (tocClicks) {
            tocClicks.removeEventListener('click', glideToSection);
            tocClicks = null;
        }
        if (tocObserver) {
            tocObserver.disconnect();
            tocObserver = null;
        }
    }

    // The older way to copy, for a page on plain http or a browser that refuses the clipboard API: the copy command on
    // a selected hidden text box, since Safari fires no copy event without a selection. The copy event sets the HTML
    // form too when there is one. False when the browser did not copy, never an exception.
    function copyByCommand(text, html) {
        var copied = false;
        var previous = document.activeElement;
        var box = document.createElement('textarea');
        box.value = text;
        box.setAttribute('readonly', '');
        box.style.position = 'fixed';
        box.style.top = '0';
        box.style.left = '-9999px';
        box.style.opacity = '0';
        function fill(e) {
            if (!e.clipboardData) {
                return;
            }
            if (html) {
                e.clipboardData.setData('text/html', html);
            }
            e.clipboardData.setData('text/plain', text);
            e.preventDefault();
            copied = true;
        }
        document.body.appendChild(box);
        document.addEventListener('copy', fill);
        try {
            box.select();
            // Without the event (an older browser), the command still copies the selected text.
            copied = document.execCommand('copy') || copied;
        } catch (e) {
            copied = false;
        } finally {
            document.removeEventListener('copy', fill);
            document.body.removeChild(box);
            if (previous && typeof previous.focus === 'function') {
                previous.focus();
            }
        }
        return copied;
    }

    // A copy whose content the server works out after the click (Copy details may read commits from TFS first). By
    // then the browser no longer counts it as the person's action and refuses the clipboard, so the write is started
    // on the click itself, with the content still to come, and copyRichAfterClick fills it in by the button's key.
    var richCopies = {};
    var richCopyWaitMs = 120000;

    function settleRichCopy(key, content) {
        var pending = richCopies[key];
        if (!pending) {
            return null;
        }
        delete richCopies[key];
        clearTimeout(pending.timer);
        if (content) {
            pending.fill(content);
        } else {
            pending.drop();
        }
        return pending.written;
    }

    function startRichCopy(key) {
        if (!navigator.clipboard || typeof ClipboardItem === 'undefined' || !window.isSecureContext) {
            return;
        }
        settleRichCopy(key, null);
        var fill, drop;
        var content = new Promise(function (resolve, reject) { fill = resolve; drop = reject; });
        var written;
        try {
            written = navigator.clipboard.write([new ClipboardItem({
                'text/html': content.then(function (c) { return new Blob([c.html], { type: 'text/html' }); }),
                'text/plain': content.then(function (c) { return new Blob([c.text], { type: 'text/plain' }); })
            })]).then(function () { return true; }, function () { return false; });
        } catch (e) {
            // This browser takes no content that is still to come; copyRichAfterClick then writes it directly.
            return;
        }
        content.catch(function () { return null; });
        richCopies[key] = {
            fill: fill,
            drop: function () { drop(new Error('dropped')); },
            written: written,
            timer: setTimeout(function () { settleRichCopy(key, null); }, richCopyWaitMs)
        };
    }

    document.addEventListener('click', function (e) {
        var button = e.target && e.target.closest ? e.target.closest('[data-rich-copy]') : null;
        if (button && !button.disabled && button.getAttribute('aria-busy') !== 'true') {
            startRichCopy(button.getAttribute('data-rich-copy'));
        }
    }, true);

    function copyRichNow(text, html) {
        if (!navigator.clipboard || typeof ClipboardItem === 'undefined') {
            return Promise.resolve(copyByCommand(text, html));
        }
        try {
            return navigator.clipboard.write([new ClipboardItem({
                'text/html': new Blob([html], { type: 'text/html' }),
                'text/plain': new Blob([text], { type: 'text/plain' })
            })]).then(function () { return true; }, function () { return copyByCommand(text, html); });
        } catch (e) {
            return Promise.resolve(copyByCommand(text, html));
        }
    }

    // ─── Public API ───

    var commandKeyBound = false;

    window.devKit = {
        // Resolves true once the text is on the clipboard and false when the browser refused, so the page can say so.
        copyToClipboard: function (text) {
            try {
                return navigator.clipboard.writeText(text).then(function () { return true; },
                    function () { return copyByCommand(text, null); });
            } catch (e) {
                return Promise.resolve(copyByCommand(text, null));
            }
        },

        // Copies two forms at once: HTML, which Excel and Word paste with its cells and links, and plain text for
        // everything else. Always resolves, false when the browser allows neither way, so the page can say so.
        copyRich: copyRichNow,

        // copyRich for a button marked data-rich-copy: fills in the write its click started, which keeps the browser's
        // permission however long the page took to work out the content.
        copyRichAfterClick: function (key, text, html) {
            var written = settleRichCopy(key, { text: text, html: html });
            if (!written) {
                return copyRichNow(text, html);
            }
            return written.then(function (copied) { return copied || copyByCommand(text, html); });
        },

        // Ends a write a click started when the page decides to copy nothing, so nothing reaches the clipboard.
        dropRichCopy: function (key) {
            settleRichCopy(key, null);
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

        // Moves focus to a dialog's button once it has something to act on, unless the person has moved it already.
        focusInDialog: function (id) {
            var el = document.getElementById(id);
            var panel = el ? el.closest('[role="dialog"]') : null;
            var active = document.activeElement;
            if (panel && (active === panel || active === null || active === document.body)) {
                el.focus();
            }
        },

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

        registerClickOutside: registerClickOutside,
        unregisterClickOutside: unregisterClickOutside,
        openDialog: openDialog,
        closeDialog: closeDialog,
        watchWideScreen: watchWideScreen,
        unwatchWideScreen: unwatchWideScreen,
        tour: { place: tourPlace, end: tourEnd },
        guideToc: { watch: watchToc, unwatch: unwatchToc }
    };
})();

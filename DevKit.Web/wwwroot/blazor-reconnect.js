// DevKit — resilient Blazor circuit startup.
//
// The stock reconnection policy retries a handful of times over a few seconds and then
// parks the tab on "Connection lost", which is why a window left open would stop responding
// and need a manual refresh. This retries with a backoff for several minutes, and only when
// the server genuinely cannot resume the circuit does it reload the page itself.
//
// The overlay is deliberately not shown the instant the circuit drops. A transient SignalR
// blip resumes in well under a second, and flashing "Reconnecting" over a loaded grid reads
// as a failure that needs a manual refresh. It appears only once a drop has outlasted the
// grace window AND a probe confirms the server itself is unreachable.

(function () {
    'use strict';

    var MAX_RETRIES = 60;
    var MIN_DELAY_MS = 500;
    var MAX_DELAY_MS = 10000;
    var RAMP_AFTER_ATTEMPTS = 10;
    var RELOAD_GRACE_MS = 1200;

    var SHOW_GRACE_MS = 2500;
    var PROBE_INTERVAL_MS = 3000;
    var PROBE_TIMEOUT_MS = 2000;
    var HEALTH_URL = '/healthz';

    var graceTimer = null;
    var probeTimer = null;
    var connectionDown = false;
    var sawServerDown = false;

    function retryDelay(attempt) {
        if (attempt < RAMP_AFTER_ATTEMPTS) return MIN_DELAY_MS * (attempt + 1);
        return MAX_DELAY_MS;
    }

    function modal() { return document.getElementById('components-reconnect-modal'); }

    // Re-probing every few seconds would otherwise rewrite the class on each tick, so only
    // touch the DOM when the state actually changes.
    function setModalState(cls) {
        var el = modal();
        if (!el || el.className === (cls || '')) return;
        el.classList.remove('components-reconnect-show', 'components-reconnect-failed', 'components-reconnect-rejected');
        if (cls) el.classList.add(cls);
    }

    function clearTimers() {
        if (graceTimer) { clearTimeout(graceTimer); graceTimer = null; }
        if (probeTimer) { clearTimeout(probeTimer); probeTimer = null; }
    }

    // A rejected circuit is gone server-side; its state cannot be recovered, so the only
    // correct move is a reload. Do it automatically rather than asking the user to.
    function reloadSoon() {
        clearTimers();
        setTimeout(function () { location.reload(); }, RELOAD_GRACE_MS);
    }

    // True when the server answers at all. The abort keeps a hung socket from holding the
    // probe open past its own interval.
    function serverReachable() {
        var controller = new AbortController();
        var abort = setTimeout(function () { controller.abort(); }, PROBE_TIMEOUT_MS);
        return fetch(HEALTH_URL, { method: 'GET', cache: 'no-store', signal: controller.signal })
            .then(function (res) { clearTimeout(abort); return res.ok; })
            .catch(function () { clearTimeout(abort); return false; });
    }

    function evaluate() {
        if (!connectionDown) return;
        serverReachable().then(function (up) {
            if (!connectionDown) return;

            // The server went away and came back, so the process restarted and the circuit
            // this tab still holds no longer exists on the other side. Reloading is the only
            // way back, and it spares the user noticing a dead page and refreshing by hand.
            if (up && sawServerDown) { reloadSoon(); return; }

            if (!up) sawServerDown = true;

            // While the server still answers, the circuit is merely resuming and the retry
            // policy will rejoin on its own — showing the overlay here would be a false alarm.
            setModalState(up ? null : 'components-reconnect-show');
            probeTimer = setTimeout(evaluate, PROBE_INTERVAL_MS);
        });
    }

    function onConnectionDown() {
        if (connectionDown) return;
        connectionDown = true;
        sawServerDown = false;
        clearTimers();
        graceTimer = setTimeout(function () { graceTimer = null; evaluate(); }, SHOW_GRACE_MS);
    }

    function onConnectionUp() {
        connectionDown = false;
        sawServerDown = false;
        clearTimers();
        setModalState(null);
    }

    // Without Blazor's own script the page is only its prerendered HTML, so it looks ready but
    // ignores every click. Say what happened instead of leaving what reads as a hang.
    function showStartupFailure() {
        var el = document.getElementById('devkit-startup-failed');
        if (el) {
            el.hidden = false;
        }
    }

    if (typeof Blazor === 'undefined' || !Blazor.start) {
        showStartupFailure();
        return;
    }

    Blazor.start({
        circuit: {
            reconnectionOptions: {
                maxRetries: MAX_RETRIES,
                retryIntervalMilliseconds: function (previousAttempts) {
                    return retryDelay(previousAttempts);
                }
            },
            reconnectionHandler: {
                onConnectionDown: onConnectionDown,
                onConnectionUp: onConnectionUp
            }
        }
    }).catch(function () {
        reloadSoon();
    });

    // Blazor raises these on the document when it gives up or the server refuses the circuit.
    document.addEventListener('components-reconnect-state-changed', function (e) {
        if (e && (e.detail === 'rejected' || e.detail === 'failed')) reloadSoon();
    });

    // Coming back to a backgrounded tab is the common case for a dropped circuit; re-probe
    // immediately instead of waiting out the next scheduled tick.
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState !== 'visible' || !connectionDown) return;
        clearTimers();
        evaluate();
    });
})();

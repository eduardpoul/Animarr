// ── Theme detection ───────────────────────────────────────────────────────
function applyTheme() {
    const stored = localStorage.getItem("animarr-theme"); // "Dark", "Light", or "System"/null
    const prefersDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
    const isDark = stored === "Dark" || (stored !== "Light" && prefersDark);
    document.documentElement.setAttribute("data-animarr-theme", isDark ? "dark" : "light");
}

applyTheme();
window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", applyTheme);

// ── Dialog state machine ──────────────────────────────────────────────────
const reconnectModal = document.getElementById("components-reconnect-modal");

function setReconnectState(state) {
    reconnectModal.setAttribute("data-reconnect-state", state);
}

reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

// Watch the countdown span so we can switch from "connecting" → "waiting"
const countdownSpan = document.getElementById("components-seconds-to-next-attempt");
new MutationObserver(() => {
    if (reconnectModal.open && countdownSpan.textContent.trim() !== "") {
        setReconnectState("waiting");
    }
}).observe(countdownSpan, { childList: true, characterData: true, subtree: true });

// ── Grace period ──────────────────────────────────────────────────────────
// Blazor fires "show" the instant the SignalR circuit drops. On a healthy
// LAN connection most blips reconnect in 200-800 ms — flashing a giant
// "Reconnecting…" modal for that long is more disruptive than the blip itself.
// We delay the actual modal display by GRACE_MS; if reconnect succeeds first
// ("hide" fires) we cancel the timer and the user never sees it.
const GRACE_MS = 3000;
let pendingShowTimer = null;

function showAfterGrace() {
    cancelPendingShow();
    pendingShowTimer = setTimeout(() => {
        pendingShowTimer = null;
        applyTheme();
        setReconnectState("connecting");
        reconnectModal.showModal();
    }, GRACE_MS);
}

function cancelPendingShow() {
    if (pendingShowTimer !== null) {
        clearTimeout(pendingShowTimer);
        pendingShowTimer = null;
    }
}

function handleReconnectStateChanged(event) {
    switch (event.detail.state) {
        case "show":
            // Don't pop the modal immediately — wait for the grace period.
            showAfterGrace();
            break;
        case "hide":
            // Reconnect succeeded — cancel any pending modal and close if it
            // already opened.
            cancelPendingShow();
            reconnectModal.removeAttribute("data-reconnect-state");
            reconnectModal.close();
            break;
        case "failed":
            // Server gave up retrying — if the modal hasn't opened yet, show
            // it now (no grace period — this is a real failure).
            cancelPendingShow();
            if (!reconnectModal.open) {
                applyTheme();
                reconnectModal.showModal();
            }
            setReconnectState("failed");
            document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            break;
        case "rejected":
            cancelPendingShow();
            location.reload();
            break;
        case "paused":
            // "paused" only happens after the user explicitly clicked away
            // for long enough that the server dropped state — show the
            // resume affordance without grace period.
            cancelPendingShow();
            applyTheme();
            setReconnectState("paused");
            reconnectModal.showModal();
            break;
        case "resume-failed":
            setReconnectState("resume-failed");
            break;
    }
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    setReconnectState("connecting");

    try {
        // Blazor.reconnect() returns true=success, false=server rejected circuit
        const successful = await Blazor.reconnect();
        if (!successful) {
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                reconnectModal.close();
            }
        }
    } catch {
        // Server still unreachable — go back to failed state
        setReconnectState("failed");
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        setReconnectState("resume-failed");
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}

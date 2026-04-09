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

function handleReconnectStateChanged(event) {
    switch (event.detail.state) {
        case "show":
            applyTheme();
            setReconnectState("connecting");
            reconnectModal.showModal();
            break;
        case "hide":
            reconnectModal.removeAttribute("data-reconnect-state");
            reconnectModal.close();
            break;
        case "failed":
            setReconnectState("failed");
            document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
            break;
        case "rejected":
            location.reload();
            break;
        case "paused":
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

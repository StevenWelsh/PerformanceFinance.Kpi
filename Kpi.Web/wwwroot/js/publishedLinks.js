// Settings > Published links: the address this browser reaches the site from (for the allowed list), and copying a
// link's URL to the clipboard.
export async function myAddress() {
    try {
        const r = await fetch("/settings/my-address", { cache: "no-store", credentials: "same-origin" });
        return r.ok ? (await r.text()).trim() : "";
    } catch { return ""; }
}

export async function copy(text) {
    try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
}

// Loaded synchronously in <head>. The server renders data-skin, data-mode and (for Light/Dark) data-bs-theme
// on <html>; in System mode this sets data-bs-theme from the device before the first paint, and follows it.
(function () {
    const root = document.documentElement;
    const dark = window.matchMedia("(prefers-color-scheme: dark)");

    function sync() {
        const mode = root.getAttribute("data-mode");
        const theme = mode === "system" ? (dark.matches ? "dark" : "light") : mode;
        if (theme && root.getAttribute("data-bs-theme") !== theme)
            root.setAttribute("data-bs-theme", theme);
    }

    sync();
    dark.addEventListener("change", sync);
    // Blazor's enhanced navigation may copy the server's attributes back onto <html>.
    new MutationObserver(sync).observe(root, { attributes: true, attributeFilter: ["data-mode", "data-bs-theme"] });

    // Called by the Settings page to show a new choice without a reload.
    window.nomadAppearance = {
        apply(skin, mode) {
            root.setAttribute("data-skin", skin);
            root.setAttribute("data-mode", mode);
            sync();
        },
    };
})();

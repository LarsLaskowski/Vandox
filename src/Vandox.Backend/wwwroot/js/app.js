// Theme and reconnect controls. Loaded as a classic script in the head, before the style sheets,
// so the stored theme is set before the first paint. It changes nothing inside the Blazor tree.
(function () {
    "use strict";

    const storageKey = "vandox-theme";
    const root = document.documentElement;

    function readStoredTheme() {
        try {
            const value = window.localStorage.getItem(storageKey);

            return value === "light" || value === "dark" ? value : null;
        } catch (error) {
            // Local storage can be unavailable (private mode, blocked site data); the page then follows the system theme.
        }

        return null;
    }

    function storeTheme(theme) {
        try {
            if (theme === null) {
                window.localStorage.removeItem(storageKey);
            } else {
                window.localStorage.setItem(storageKey, theme);
            }
        } catch (error) {
            // Without local storage the choice lasts until the page is reloaded.
        }
    }

    function nextTheme(current) {
        if (current === "light") {
            return "dark";
        }

        return current === "dark" ? null : "light";
    }

    function applyTheme(theme) {
        if (theme === null) {
            delete root.dataset.theme;
        } else {
            root.dataset.theme = theme;
        }
    }

    function switchTheme() {
        const current = root.dataset.theme === undefined ? null : root.dataset.theme;
        const theme = nextTheme(current);

        applyTheme(theme);
        storeTheme(theme);
        window.dispatchEvent(new Event("vandox-themechange"));
    }

    function retryConnection() {
        window.Blazor.reconnect().then(function (connected) {
            if (connected === false) {
                window.location.reload();
            }
        }, function () {
            window.location.reload();
        });
    }

    const stored = readStoredTheme();

    if (stored !== null) {
        applyTheme(stored);
    }

    document.addEventListener("click", function (event) {
        const target = event.target instanceof Element ? event.target : null;

        if (target === null) {
            return;
        }

        if (target.closest("[data-theme-toggle]") !== null) {
            switchTheme();
        } else if (target.closest("[data-reconnect-retry]") !== null) {
            retryConnection();
        } else if (target.closest("[data-reload]") !== null) {
            window.location.reload();
        }
    });
})();

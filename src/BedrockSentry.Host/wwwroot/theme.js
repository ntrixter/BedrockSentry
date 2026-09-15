// Theme switching.
//
// The server writes the correct theme onto <html> before the page is sent, so
// this only handles the click. Flipping the attribute here rather than posting
// and reloading keeps the switch instant; the cookie is what makes the *next*
// server render agree, which is what prevents a flash of the wrong theme on
// every subsequent navigation.
//
// Deliberately plain DOM rather than Blazor interop: the sign-in and first-run
// pages are static server-rendered and have no circuit to call into.

window.bedrockSentryTheme = (function () {
    const COOKIE = 'bedrocksentry-theme';
    const ONE_YEAR = 60 * 60 * 24 * 365;

    function apply(theme) {
        const root = document.documentElement;
        root.setAttribute('data-theme', theme);

        // Drives Bootstrap 5.3's own dark mode from the same switch, so form
        // controls and buttons follow without being restyled by hand.
        root.setAttribute('data-bs-theme', theme);
    }

    return {
        toggle: function () {
            const current = document.documentElement.getAttribute('data-theme');
            const next = current === 'light' ? 'dark' : 'light';

            apply(next);

            // SameSite=Lax so it survives ordinary navigation. Not marked Secure:
            // an operator reaching the panel over plain loopback during setup
            // would otherwise silently fail to keep their choice.
            document.cookie =
                COOKIE + '=' + next + ';path=/;max-age=' + ONE_YEAR + ';samesite=lax';
        }
    };
})();

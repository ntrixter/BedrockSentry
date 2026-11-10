// Brings the first element a selector matches into view: a restore panel opened from
// a row far down the Backups list opens under the tab's four cards and the page
// scrolls up to it; once a restore starts, the page scrolls to the server's controls
// and the strip under them (D111).
window.bedrockSentryScrollTo = function (selector) {
    const el = document.querySelector(selector);

    if (el) {
        el.scrollIntoView({ block: 'start', behavior: 'smooth' });
    }
};

// Selects a text box's value when it has the focus: a name filled in after the box was
// reached by Tab is replaced by typing rather than appended to (T424 clause 3).
window.bedrockSentrySelectIfFocused = function (el) {
    if (el && document.activeElement === el) {
        el.select();
    }
};

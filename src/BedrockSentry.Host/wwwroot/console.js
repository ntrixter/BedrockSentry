// Keeps the console pinned to the newest output — but only when the operator is
// already at the bottom. Scrolling up is a deliberate act, usually to read the
// thing that just went wrong, and yanking the view back down would make the log
// unreadable exactly when it matters most.
//
// Whether they are "at the bottom" is remembered from their own scrolling, not
// measured when new output arrives. Measuring after the fact cannot work: by the
// time this is asked, the new lines are already in the DOM, so the view is
// exactly as far from the bottom as the batch is tall. Three lines clears any
// sane threshold, and a restart arrives in batches of forty — which is why the
// console followed nothing at all.
//
// A scroll event fires when the operator scrolls, and when this code scrolls.
// Both are correct inputs: the second always lands at the bottom and so leaves
// the console pinned. Appending content fires no scroll event, which is exactly
// why the remembered answer survives it.
window.bedrockSentryConsole = {
    attach: function (id) {
        const el = document.getElementById(id);
        if (!el) {
            return;
        }

        // Blazor can hand back the same element when a tab is reopened. Two
        // listeners would not be wrong, just wasteful, and they accumulate.
        if (!el.dataset.sentryAttached) {
            el.dataset.sentryAttached = "1";

            el.addEventListener("scroll", function () {
                // A few pixels of slack: browsers report fractional scroll
                // positions at non-integer zoom levels, so an exact comparison
                // never matches.
                const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
                el.dataset.sentryPinned = atBottom ? "1" : "";
            }, { passive: true });
        }

        // Opening the console means "show me what is happening now", so it
        // starts pinned however much history is already in the buffer.
        el.dataset.sentryPinned = "1";
        el.scrollTop = el.scrollHeight;
    },

    follow: function (id) {
        const el = document.getElementById(id);
        if (el && el.dataset.sentryPinned) {
            el.scrollTop = el.scrollHeight;
        }
    },

    // The viewer's show or hide per boxed notice (ConsoleNoticeChoices), kept in this
    // browser per instance, so hiding a warning on one server leaves it on the others. Storage can be refused (a private window, blocked
    // site data); then nothing is kept and each notice keeps its default.
    noticeChoices: function (instanceId) {
        try {
            return window.localStorage.getItem("bedrockSentry.consoleNotices." + instanceId);
        } catch (e) {
            return null;
        }
    },

    saveNoticeChoices: function (instanceId, json) {
        try {
            window.localStorage.setItem("bedrockSentry.consoleNotices." + instanceId, json);
        } catch (e) {
            // The choice holds for this page only.
        }
    }
};

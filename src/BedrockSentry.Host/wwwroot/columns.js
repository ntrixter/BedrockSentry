// Drag-to-resize for a panel table's columns, and remembering the result.
//
// WHY A <colgroup> AND NOT th.style.width. Under `table-layout: fixed` the first
// row of the table decides every column, and a <col> is that row's authority
// without being a cell. Writing the width onto the <th> works until Blazor
// re-renders the header — which it does on every refresh of the tab — and then
// the widths are gone with no event to react to. The <col> elements are markup
// the component owns, so a re-render restores them and this only has to write
// into them once per attach.
//
// WHY THE WIDTHS ARE PERCENTAGES. The panel is read at whatever width the window
// happens to be, and a column pinned at 340px is a third of a narrow window and a
// sixth of a wide one. Dragging is measured in pixels because that is what a
// pointer does; what gets stored is the share of the table it ended up being.
//
// A DRAG MOVES A BORDER, SO IT TAKES FROM THE COLUMN ON THE OTHER SIDE OF IT.
// Widening one column alone leaves the others holding the shares they had, the
// percentages then sum to more than the whole, and the browser scales all of them
// back down to fit — so a 120px drag moves the border 98px and an arrow key aimed
// right makes the column narrower, each step compounding the last. Measured, both
// of them. Changing the pair keeps the total fixed, which is also the only way the
// border ends up under the pointer that dragged it.
//
// WHAT IS DELIBERATELY NOT HERE. No minimum beyond one that stops a column
// vanishing, and no maximum: an administrator dragging a column to almost nothing
// has said what they want, and a table that fights back is worse than one that
// lets them do it and remembers. Reset is a double-click on the grip, and it puts
// back the width the MARKUP asked for — blanking the inline width instead removes
// the default with it, which under a fixed layout gives every column an equal
// share of the table.
window.bedrockSentryColumns = {
    // The floor, in pixels. Below this a column cannot be grabbed again, because
    // its own grip has nowhere to sit.
    minimum: 44,

    attach: function (tableId, storageKey) {
        const table = document.getElementById(tableId);
        if (!table) {
            return;
        }

        const cols = Array.from(table.querySelectorAll("colgroup > col"));
        const heads = Array.from(table.querySelectorAll("thead th"));

        // The markup guard in the test suite pins these two as equal. If they ever
        // are not, every width lands on the wrong column, so this does nothing at
        // all rather than something arbitrary.
        if (cols.length === 0 || cols.length !== heads.length) {
            return;
        }

        // Before restore, and once ever: these are what a reset goes back to, and
        // the stored widths are about to be written over them.
        cols.forEach(function (col) {
            if (col.dataset.sentryDefault === undefined) {
                col.dataset.sentryDefault = col.style.width || "";
            }
        });

        this.restore(cols, storageKey);

        // Blazor hands back the same table element when the tab re-renders, and a
        // second set of grips would stack on the first.
        if (table.dataset.sentryColumns) {
            return;
        }
        table.dataset.sentryColumns = "1";

        // The last column has no border to the right of it that is inside the
        // table, so it is sized by whatever the others leave.
        for (let i = 0; i < heads.length - 1; i++) {
            this.addGrip(table, cols, heads, i, storageKey);
        }
    },

    addGrip: function (table, cols, heads, index, storageKey) {
        const self = this;
        const grip = document.createElement("button");

        grip.type = "button";
        grip.className = "col-grip";
        grip.tabIndex = 0;

        // One pixel of visible line and no text, so it needs a name of its own.
        const label = heads[index].textContent.trim();
        grip.setAttribute(
            "aria-label",
            label ? "Resize the " + label + " column" : "Resize column " + (index + 1));

        heads[index].appendChild(grip);

        let startX = 0;
        let startLeft = 0;
        let startRight = 0;

        const move = function (event) {
            self.place(table, cols, index, startLeft, startRight, event.clientX - startX);
        };

        const up = function () {
            document.removeEventListener("pointermove", move);
            document.removeEventListener("pointerup", up);
            table.classList.remove("is-resizing");
            self.remember(cols, storageKey);
        };

        grip.addEventListener("pointerdown", function (event) {
            // Left button only. A right-click here is a context menu, and a
            // middle-click is a paste on some platforms.
            if (event.button !== 0) {
                return;
            }

            event.preventDefault();
            startX = event.clientX;
            startLeft = heads[index].getBoundingClientRect().width;
            startRight = heads[index + 1].getBoundingClientRect().width;
            table.classList.add("is-resizing");

            document.addEventListener("pointermove", move);
            document.addEventListener("pointerup", up);
        });

        // The same operation without a pointer. A grip that can only be dragged is
        // a control somebody navigating by keyboard cannot reach at all.
        grip.addEventListener("keydown", function (event) {
            const step = event.shiftKey ? 32 : 8;
            let delta = 0;

            if (event.key === "ArrowLeft") {
                delta = -step;
            } else if (event.key === "ArrowRight") {
                delta = step;
            } else {
                return;
            }

            event.preventDefault();

            self.place(
                table,
                cols,
                index,
                heads[index].getBoundingClientRect().width,
                heads[index + 1].getBoundingClientRect().width,
                delta);

            self.remember(cols, storageKey);
        });

        // Back to whatever the markup asked for. Double-click rather than a button
        // of its own: a reset control per column is five more things on a header
        // that is meant to be read.
        grip.addEventListener("dblclick", function (event) {
            event.preventDefault();
            cols.forEach(function (col) { col.style.width = col.dataset.sentryDefault || ""; });
            self.forget(storageKey);
        });
    },

    /// Moves the border between `index` and the column after it by `delta` pixels,
    /// leaving the pair's combined width exactly as it was.
    place: function (table, cols, index, startLeft, startRight, delta) {
        // Clamped against BOTH floors before either is written, so a drag past the
        // end of the neighbour stops rather than trading one unusable column for
        // another.
        const room = delta >= 0
            ? Math.min(delta, startRight - this.minimum)
            : Math.max(delta, this.minimum - startLeft);

        const moved = Math.max(this.minimum - startLeft, Math.min(room, startRight - this.minimum));

        cols[index].style.width = this.share(table, startLeft + moved);
        cols[index + 1].style.width = this.share(table, startRight - moved);
    },

    /// A pixel width as a percentage of the table it is in.
    share: function (table, pixels) {
        const total = table.getBoundingClientRect().width;
        return total > 0 ? (pixels / total * 100).toFixed(3) + "%" : pixels + "px";
    },

    remember: function (cols, storageKey) {
        // Only the columns that have been dragged are stored. A column still at its
        // markup width stays at its markup width, so changing a default later
        // reaches everybody who never touched that column.
        const widths = cols.map(function (col) { return col.style.width || null; });

        try {
            window.localStorage.setItem(storageKey, JSON.stringify(widths));
        } catch (e) {
            // Private browsing, blocked site data, or a full quota. Losing the
            // widths is not worth a broken page, and the drag itself has already
            // happened.
        }
    },

    restore: function (cols, storageKey) {
        let widths = null;

        try {
            widths = JSON.parse(window.localStorage.getItem(storageKey) || "null");
        } catch (e) {
            // Unreadable or hand-edited. Treated as nothing stored.
        }

        // Length is checked as well as shape: a stored set from before a column was
        // added would otherwise shift every width one place along.
        if (!Array.isArray(widths) || widths.length !== cols.length) {
            return;
        }

        cols.forEach(function (col, i) {
            if (typeof widths[i] === "string" && widths[i]) {
                col.style.width = widths[i];
            }
        });
    },

    forget: function (storageKey) {
        try {
            window.localStorage.removeItem(storageKey);
        } catch (e) {
            // As above.
        }
    }
};

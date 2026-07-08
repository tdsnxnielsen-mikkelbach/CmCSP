// Interactive product tour (guided walkthrough) built on driver.js.
//
// The tour is data-driven: any element on the current page decorated with a
// `data-tour` attribute becomes a step. This keeps every page's tour defined
// inline in its own markup, so the same script drives all pages and each page
// can be (re)started independently via the per-page "Take a tour" button.
//
//   data-tour="20"                 (required) numeric order across the whole page
//   data-tour-title="…"            step heading
//   data-tour-desc="…"             step body (simple HTML allowed)
//   data-tour-side="right|left|top|bottom"   optional popover placement
//   data-tour-align="start|center|end"       optional popover alignment
//
// Shared chrome (nav drawer, filters, theme, refresh) is decorated once in the
// layout with low/high order numbers so it frames every page's own steps.
(function () {
    "use strict";

    const STORAGE_KEY = "cmcsp-tour-seen";

    function driverCtor() {
        return window.driver && window.driver.js && window.driver.js.driver;
    }

    function isVisible(el) {
        return !!(el && el.offsetParent !== null);
    }

    // Collect visible [data-tour] elements on the current page, ordered, and
    // map them to driver.js steps. A leading welcome step is always prepended.
    function buildSteps() {
        const nodes = Array.prototype.slice.call(document.querySelectorAll("[data-tour]"));
        const visible = nodes.filter(isVisible);

        visible.sort(function (a, b) {
            const oa = parseFloat(a.getAttribute("data-tour")) || 0;
            const ob = parseFloat(b.getAttribute("data-tour")) || 0;
            return oa - ob;
        });

        const steps = [
            {
                popover: {
                    title: "Welcome — guided tour",
                    description:
                        "A quick walkthrough of this page. Use <b>Next</b> / <b>Back</b> to step through, or press <b>Esc</b> to exit. You can replay it any time from the <b>Take a tour</b> button.",
                },
            },
        ];

        visible.forEach(function (el) {
            const popover = {
                title: el.getAttribute("data-tour-title") || "",
                description: el.getAttribute("data-tour-desc") || "",
            };
            const side = el.getAttribute("data-tour-side");
            const align = el.getAttribute("data-tour-align");
            if (side) {
                popover.side = side;
            }
            if (align) {
                popover.align = align;
            }
            // driver.js accepts an Element directly, so no id juggling is needed.
            steps.push({ element: el, popover: popover });
        });

        return steps;
    }

    // Start (or restart) the tour for whatever page is currently shown.
    function start() {
        const ctor = driverCtor();
        if (!ctor) {
            console.warn("driver.js not loaded; product tour unavailable.");
            return;
        }

        const steps = buildSteps();

        const tour = ctor({
            showProgress: true,
            allowClose: true,
            overlayOpacity: 0.6,
            nextBtnText: "Next",
            prevBtnText: "Back",
            doneBtnText: "Got it",
            steps: steps,
            onDestroyed: function () {
                try {
                    localStorage.setItem(STORAGE_KEY, "1");
                } catch (e) {
                    /* storage unavailable — ignore */
                }
            },
        });

        tour.drive();
    }

    // Auto-start once for first-time visitors. Waits briefly so the page has
    // rendered its tour targets before highlighting them.
    function maybeAutoStart() {
        let seen = null;
        try {
            seen = localStorage.getItem(STORAGE_KEY);
        } catch (e) {
            /* storage unavailable — treat as seen to avoid nagging */
            seen = "1";
        }

        if (seen) {
            return;
        }

        window.setTimeout(start, 800);
    }

    window.cmcspTour = { start: start, maybeAutoStart: maybeAutoStart };
})();

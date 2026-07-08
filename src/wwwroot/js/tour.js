// Interactive product tour (guided walkthrough) built on driver.js.
// Exposes window.cmcspTour with start() for the "Take a tour" button and
// maybeAutoStart() for a one-time first-visit walkthrough.
(function () {
    "use strict";

    const STORAGE_KEY = "cmcsp-tour-seen";

    // Full ordered list of steps. Any step whose element is missing on the
    // current page is skipped, so the same tour works on every route.
    function allSteps() {
        return [
            {
                popover: {
                    title: "Welcome to the CSP Cost Dashboard",
                    description:
                        "Take a quick tour of the main features. Use <b>Next</b> to step through, or press <b>Esc</b> to exit at any time.",
                },
            },
            {
                element: "#tour-menu-button",
                popover: {
                    title: "Navigation drawer",
                    description: "Toggle the side navigation to move between the dashboard pages.",
                },
            },
            {
                element: "#tour-nav",
                popover: {
                    title: "Dashboard pages",
                    description:
                        "Jump to cost overviews, budgets, breakdowns by subscription or resource group, tag chargeback, trends, optimization, security and more.",
                    side: "right",
                    align: "start",
                },
            },
            {
                element: "#tour-sub-picker",
                popover: {
                    title: "Subscription filter",
                    description:
                        "Narrow every page to specific tenants and subscriptions. This is a view filter — it never widens what you're allowed to see.",
                },
            },
            {
                element: "#tour-date-range",
                popover: {
                    title: "Date range",
                    description:
                        "Set the reporting window for all charts and totals. Use the fit-to-data button to snap to the available range.",
                },
            },
            {
                element: "#tour-kpis",
                popover: {
                    title: "Headline numbers",
                    description: "At-a-glance totals for the selected range, month-to-date, year-to-date and average daily cost.",
                },
            },
            {
                element: "#tour-charts",
                popover: {
                    title: "Cost charts",
                    description: "Interactive trends and breakdowns. Charts update automatically as you change the filters above.",
                },
            },
            {
                element: "#tour-refresh",
                popover: {
                    title: "Refresh data",
                    description: "Re-fetch the latest collected cost data on demand.",
                    side: "right",
                    align: "start",
                },
            },
            {
                element: "#tour-theme",
                popover: {
                    title: "Light / dark mode",
                    description: "Switch between light and dark themes to suit your preference.",
                },
            },
            {
                element: "#tour-take-tour",
                popover: {
                    title: "You're all set",
                    description: "Replay this tour any time from here. Enjoy exploring your Azure spend!",
                },
            },
        ];
    }

    // Keep steps that either have no element (intro/centered) or whose element
    // is currently present and visible in the DOM.
    function visibleSteps() {
        return allSteps().filter(function (step) {
            if (!step.element) {
                return true;
            }
            const el = document.querySelector(step.element);
            return !!(el && el.offsetParent !== null);
        });
    }

    function driverCtor() {
        return window.driver && window.driver.js && window.driver.js.driver;
    }

    function start() {
        const ctor = driverCtor();
        if (!ctor) {
            console.warn("driver.js not loaded; product tour unavailable.");
            return;
        }

        const steps = visibleSteps();
        if (steps.length === 0) {
            return;
        }

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

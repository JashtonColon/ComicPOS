// Small activity watcher for the register's inactivity auto-lock
// (CurrentWorkerContext/MainLayout - worker PIN + 1-minute lockout,
// added 2026-11-24). Lives as a real static file loaded from App.razor's
// own <script> tag (next to _framework/blazor.web.js), NOT as an inline
// <script> tag written inside a Razor component - a <script> inserted
// through Blazor's own render-tree diffing is not guaranteed to execute
// the way a normal page-parsed <script> does, which would make the whole
// lockout silently never arm. A real static file sidesteps that entirely.
window.comicPosActivityWatch = {
    start: function (dotnetRef) {
        let lastSent = 0;
        const notify = () => {
            const now = Date.now();
            // Throttled client-side - this only needs to know "was there
            // ANY activity in roughly the last few seconds", not every
            // single mousemove pixel, which would be a SignalR round-trip
            // per pixel if sent unthrottled.
            if (now - lastSent > 4000) {
                lastSent = now;
                dotnetRef.invokeMethodAsync('NotifyActivity');
            }
        };
        ['mousemove', 'mousedown', 'keydown', 'scroll', 'touchstart', 'click'].forEach(evt =>
            document.addEventListener(evt, notify, { passive: true }));
    }
};

// Cover-zoom "don't look pixelated" fix (2026-11-24, follow-up to the
// zoom-image size fix in app.css's .zoom-overlay img rule). That CSS
// forces an explicit width/height on the zoomed <img> so it actually
// scales UP past its natural pixel size (max-width/max-height alone can
// only ever shrink, never enlarge - see that rule's own doc comment) -
// but a flat "always stretch to ~1200px" cap made a small
// distributor-thumbnail-resolution cover (most covers on file here are a
// few hundred pixels wide at most) look visibly blocky once stretched
// that far, which is exactly what got reported after the first fix
// shipped. Pure CSS can't see an image's real natural pixel size to
// decide how far is "too far" - this does, via plain JS naturalWidth/
// naturalHeight, and sets an inline style (which wins over the CSS rule)
// capping the upscale at 3x the cover's own real resolution, still
// bounded by the same 92% - viewport/1200px ceiling as before. A big,
// genuinely high-res cover scan still fills that ceiling exactly like
// before; a small thumbnail now stops enlarging at a point that still
// looks reasonably sharp instead of stretching all the way to the cap.
// No DotNet round-trip needed - this only touches the DOM, so it's pure
// client-side, driven by a MutationObserver watching for the zoom
// overlay's <img> appearing (ImageZoomOverlay.razor only renders it at
// all while a cover is zoomed) or its src attribute changing (clicking a
// different cover while already zoomed re-uses the same <img> element).
(function () {
    function applySize(img) {
        if (!img || !img.naturalWidth || !img.naturalHeight) return;
        var maxW = Math.min(window.innerWidth * 0.92, 1200);
        var maxH = Math.min(window.innerHeight * 0.92, 1200);
        // Smaller natives get a gentler upscale instead of the full 3x - a
        // flat 3x cap made a genuinely small source photo (confirmed as
        // low as 180px wide, on a brand-new small-press title Comic Vine
        // doesn't even carry - see UpgradeLowResolutionCovers's "no
        // confident Comic Vine match for the series" outcome - so there's
        // no sharper copy to fetch for it, ever) stretch into visible
        // mush. Ramps smoothly from 1.5x at a tiny native size up to the
        // full 3x once a cover reaches 600px wide - the same number
        // AutoFetchService's LowResCoverWidthThreshold uses to call a
        // cover "decent" - so a cover that already passes that check
        // still gets exactly the zoom treatment it always had.
        var multiplier = Math.min(3, 1.5 + img.naturalWidth / 400);
        var capW = Math.min(maxW, img.naturalWidth * multiplier);
        var capH = Math.min(maxH, img.naturalHeight * multiplier);
        var scale = Math.min(capW / img.naturalWidth, capH / img.naturalHeight);
        img.style.width = Math.round(img.naturalWidth * scale) + 'px';
        img.style.height = Math.round(img.naturalHeight * scale) + 'px';
    }
    function tryApply() {
        var img = document.querySelector('.zoom-overlay img');
        if (!img) return;
        if (img.complete && img.naturalWidth) applySize(img);
        else img.addEventListener('load', function () { applySize(img); }, { once: true });
    }
    if (document.body) {
        new MutationObserver(tryApply).observe(document.body, {
            childList: true, subtree: true, attributes: true, attributeFilter: ['src']
        });
    }
})();

// App-wide "Escape closes whatever's open" (2026-11-25). Modal.razor's own
// per-dialog Escape handler only fires when that dialog's box actually has
// DOM focus, and its own doc comment already admits a FocusAsync() call
// can silently lose that race on some browsers/timings - exactly what got
// reported live on a New Book Detail popup (Add to a Subscriber's Pull
// List). ImageZoomOverlay (the full-screen cover zoom) never had Escape
// support at all - it only closes on a click. A single document-level
// listener sidesteps both: it doesn't depend on what currently has focus,
// and it covers every overlay in the app from one place instead of a
// handler wired into each one.
//
// Closes the TOPMOST thing open, one layer at a time, never more than one
// per keypress: a zoomed cover (.zoom-overlay) is always the frontmost
// layer when present (it's only ever opened from on top of something
// else), so it's checked first; otherwise the last .modal-backdrop in
// document order is whichever dialog was opened most recently (a nested
// dialog's own markup is always written - and therefore rendered - after
// its parent's), so closing just that one leaves any dialog underneath it
// open, matching how dismissing a stack of dialogs is expected to behave.
document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape') return;

    var zooms = document.querySelectorAll('.zoom-overlay');
    if (zooms.length > 0) {
        zooms[zooms.length - 1].click();
        return;
    }

    var closeButtons = document.querySelectorAll('.modal-backdrop .modal-close');
    if (closeButtons.length > 0) {
        closeButtons[closeButtons.length - 1].click();
    }
});


// Background-scroll-lock fix for every modal in the app (2026-11-26,
// reported live on New Book Detail's cover popup: "when the covers page
// is up you should not be able to scroll the background. this only
// happens when there is 4 or less covers"). Modal.razor's own CSS
// (.modal-backdrop) never set its own overflow, and relied entirely on
// the modal's CONTENT being tall enough that .modal-box's internal
// .modal-body { overflow-y: auto } had something of its own to scroll -
// when a modal's content is short (confirmed: 4 or fewer cover thumbnails
// don't fill .modal-box's max-height: 90vh), .modal-body has nothing to
// scroll, so the browser's normal scroll-chaining behavior sends the
// wheel/touch input straight past both .modal-body and the backdrop
// (which also isn't scrollable - same reason) to the real document
// underneath, scrolling the page behind the modal instead. A tall modal
// never showed this because its own internal scrollbar absorbed the
// input first - the content height was accidentally doing the job a
// real scroll lock should have been doing all along.
//
// Same "watch the DOM, not a per-component Blazor round trip" approach
// as the cover-zoom-size fix above: every modal in the app already goes
// through Modal.razor's one .modal-backdrop class (see that component's
// own doc comment), so one observer here covers all of them - current
// and future - without wiring IJSRuntime into each one individually.
// Locks document.body's own scroll (html/body is the real scrolling
// element in this layout - app.css's .pos-main has no overflow of its
// own) whenever at least one modal is open, and only releases it once
// the LAST one closes, so a modal opened from inside another modal (e.g.
// New Book Detail's own "Add to a Subscriber's Pull List") doesn't
// unlock the background the moment just the inner one closes.
(function () {
    function syncBodyScrollLock() {
        var anyModalOpen = document.querySelector('.modal-backdrop') !== null;
        document.body.style.overflow = anyModalOpen ? 'hidden' : '';
    }
    if (document.body) {
        new MutationObserver(syncBodyScrollLock).observe(document.body, {
            childList: true, subtree: true
        });
        syncBodyScrollLock();
    }
})();

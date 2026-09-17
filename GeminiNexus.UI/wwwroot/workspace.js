let dotnet, observer, newerObserver, scroll, follow = true, anchor;
export function disconnect() {}
export function setup(reference, prefetchPages) {
    dotnet = reference; observer?.disconnect(); newerObserver?.disconnect();
    scroll = document.getElementById('nexus-scroll');
    if (!scroll) return;
    let previousTop = scroll.scrollTop, fetching = false;
    scroll.onscroll = () => {
        const delta = scroll.scrollTop - previousTop; previousTop = scroll.scrollTop;
        const remaining = scroll.scrollHeight - scroll.scrollTop - scroll.clientHeight;
        follow = remaining < 120;
        if (fetching || prefetchPages <= 0 || !delta) return;
        if ((delta < 0 && scroll.scrollTop < 200) || (delta > 0 && remaining < 200)) {
            fetching = true;
            reference.invokeMethodAsync('Prefetch', delta > 0).catch(() => {}).finally(() => { fetching = false; });
        }
    };
    document.querySelector('.composer textarea')?.addEventListener('keydown', preventEnter);
}
function preventEnter(event) { if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) event.preventDefault(); }
export function bottom(force) { requestAnimationFrame(() => { if (scroll && (force || follow)) { scroll.scrollTop = scroll.scrollHeight; follow = true; } }); }
export function sidebarNearEnd() { const el = document.querySelector('.conversation-list'); return el && el.scrollHeight - el.scrollTop - el.clientHeight < 150; }
export function captureAnchor() {
    if (!scroll) return;
    const top = scroll.getBoundingClientRect().top;
    const visible = [...scroll.querySelectorAll('[data-message]')].find(e => e.getBoundingClientRect().bottom > top);
    anchor = visible ? { id: visible.dataset.message, offset: visible.getBoundingClientRect().top - top } : null;
}
export function restoreAnchor() {
    requestAnimationFrame(() => requestAnimationFrame(() => {
        if (!anchor || !scroll) return;
        const element = [...scroll.querySelectorAll('[data-message]')].find(e => e.dataset.message === anchor.id);
        if (element) scroll.scrollTop += element.getBoundingClientRect().top - scroll.getBoundingClientRect().top - anchor.offset;
        anchor = null;
    }));
}
export function dispose() { disconnect(); observer?.disconnect(); newerObserver?.disconnect(); if (scroll) scroll.onscroll = null; document.querySelector('.composer textarea')?.removeEventListener('keydown', preventEnter); dotnet = null; }

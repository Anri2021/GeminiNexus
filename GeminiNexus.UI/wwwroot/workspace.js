let dotnet, observer, scroll, follow = true, anchor;
export function disconnect() {}
export function setup(reference, interval) {
    dotnet = reference; observer?.disconnect();
    scroll = document.getElementById('nexus-scroll');
    if (!scroll) return;
    scroll.onscroll = () => { follow = scroll.scrollHeight - scroll.scrollTop - scroll.clientHeight < 120; };
    const sentinel = document.getElementById('history-sentinel');
    if (sentinel) {
        observer = new IntersectionObserver(entries => {
            if (entries.some(e => e.isIntersecting)) reference.invokeMethodAsync('Older').catch(() => {});
        }, { root: scroll, rootMargin: '200px 0px 0px 0px' });
        observer.observe(sentinel);
    }
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
export function dispose() { disconnect(); observer?.disconnect(); if (scroll) scroll.onscroll = null; document.querySelector('.composer textarea')?.removeEventListener('keydown', preventEnter); dotnet = null; }

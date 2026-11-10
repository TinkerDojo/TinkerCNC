// The system's own notifications (the browser's Notification API) for the dashboard's notifications.
// Browsers offer them only to secure pages (HTTPS, or this computer as localhost) and only while a tab is open.

const shown = new Map();

/** "granted", "denied", "default" (not asked yet) or "unsupported". */
export function permission() {
    return 'Notification' in window && window.isSecureContext ? Notification.permission : 'unsupported';
}

export async function requestPermission() {
    if (permission() === 'unsupported') return 'unsupported';
    return await Notification.requestPermission();
}

/**
 * Wires the "show system notifications" checkbox. Ticking it asks for permission right in the browser's own event
 * handler: browsers show their permission prompt only while handling the user's action, which a round trip to the
 * server would have ended. The box stays ticked only when permission was given; .NET learns the outcome.
 */
export function watchToggle(input, dotnet) {
    input.addEventListener('change', async () => {
        const wanted = input.checked;
        const before = permission();
        const asked = performance.now();
        const result = wanted ? await requestPermission() : before;
        input.checked = wanted && result === 'granted';
        // A refusal faster than anyone can click means the browser answered itself: the site is blocked (an earlier
        // "Block", or prompts dismissed too often), and only its site settings can change that.
        const refusedWithoutAsking = wanted && result === 'denied' && (before === 'denied' || performance.now() - asked < QuickestAnswerMs);
        await dotnet.invokeMethodAsync('ToggledAsync', wanted, result, refusedWithoutAsking);
    });
}

const QuickestAnswerMs = 400;

/**
 * Shows one notification; the tag is the dashboard notification's id, so several open tabs show it once.
 * A click brings the dashboard to the front on the given page. Resolves to what the browser reported:
 * "shown", "failed" (it could not), "silent" (it took the notification but never said it showed it: the system's
 * settings or Do Not Disturb usually swallow it then) or "blocked" (no permission).
 */
export function show(tag, title, body, url, sticky) {
    if (permission() !== 'granted') return Promise.resolve('blocked');
    let note;
    try {
        note = new Notification(title, {
            body,
            tag,
            icon: new URL('favicon.png', document.baseURI).href,
            requireInteraction: sticky,
        });
    } catch (error) {
        // Some browsers (Chrome on Android) allow notifications only through a service worker.
        console.warn('System notification could not be shown', error);
        return Promise.resolve('failed');
    }
    note.onclick = () => {
        window.focus();
        if (url) Blazor.navigateTo(url);
        note.close();
    };
    note.onclose = () => shown.delete(tag);
    shown.set(tag, note);
    return new Promise(resolve => {
        const silent = setTimeout(() => resolve('silent'), ShowWaitMs);
        note.onshow = () => { clearTimeout(silent); resolve('shown'); };
        note.onerror = () => { clearTimeout(silent); resolve('failed'); };
    });
}

/** How long the browser gets to report that it showed a notification. */
const ShowWaitMs = 3000;

/** Takes a notification away once it was answered or the machine resolved it. */
export function close(tag) {
    shown.get(tag)?.close();
    shown.delete(tag);
}

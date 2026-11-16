using System.Text.RegularExpressions;
using Microsoft.Playwright;

/// <summary>
/// Presentation helpers for a recorded page. Playwright's videos show no mouse pointer, so an
/// overlay script (installed on every page load) draws one that follows the real mouse, a ripple
/// on each click, a spotlight around whatever matters, a caption bar and full-screen title cards.
/// Every action goes through the real UI: real mouse moves and clicks, real typing.
/// </summary>
sealed class Stage(IPage page, IBrowser browser, string baseUrl, double pace = 1.0)
{
    // The feel of the video. Every value below is scaled by --pace (1.0 = as written; 1.5 = half again as slow).
    private const int GlideBaseMs = 600;         // pointer glide to a click target: a base time...
    private const double GlideMsPerPx = 1.1;     // ...plus this per pixel travelled (900px is about 1.6s)
    private const int PreClickHighlightMs = 800; // outline on a target before it's clicked
    private const int AfterClickMs = 600;
    private const int KeyDelayMs = 90;           // typing speed
    private const int AfterTypingMs = 400;
    private const int ScrollSettleMs = 350;
    private const int AfterSpotlightMs = 250;
    private const int AfterCardMs = 400;
    private const int NoteBaseMs = 1800;         // how long a note stays up: a base time...
    private const int NoteMsPerWord = 300;       // ...plus this per word (about 200 words a minute)
    private const int AfterNoteMs = 350;

    private double _x = 800, _y = 500; // where the pointer is (matches the overlay's starting point)

    /// <summary>Scales a presentation delay by --pace. Waits for the app itself (page loads, texts arriving) aren't scaled.</summary>
    private int P(int ms) => (int)Math.Round(ms * pace);

    public IPage Page => page;

    public string Url(string path) => baseUrl.TrimEnd('/') + path;

    // ---------- Navigation ----------

    public async Task GoAsync(string path)
    {
        await page.GotoAsync(path.StartsWith("http") ? path : Url(path));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>Opens an interactive (Blazor Server) page and waits until its buttons work.</summary>
    public async Task GoInteractiveAsync(string path)
    {
        await GoAsync(path);
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await page.WaitForTimeoutAsync(1500); // the prerendered page is live once the circuit connects
    }

    // ---------- Overlay ----------

    public Task CaptionAsync(string title, string detail = "") =>
        page.EvaluateAsync("([t, d]) => window.__rec.caption(t, d)", new[] { title, detail });

    public Task HideCaptionAsync() => page.EvaluateAsync("() => window.__rec.caption(null)");

    /// <summary>A small label that stays in a corner for the whole recording (until changed).</summary>
    public Task BadgeAsync(string text) => page.EvaluateAsync("t => window.__rec.badge(t)", text);

    /// <summary>
    /// Stops the action and explains something: dims the page, outlines the target (if any) and shows a
    /// note beside it, for long enough to read (or <paramref name="ms"/>). With no target, the note is centered.
    /// A locator that matches several elements rings them all with one outline.
    /// </summary>
    public async Task NoteAsync(ILocator? target, string title, string body = "", int? ms = null)
    {
        if (target is not null)
        {
            await CenterAsync(target.First);
            await target.EvaluateAllAsync<object?>("(els, a) => { window.__rec.note(els, a[0], a[1]); return null; }", new[] { title, body });
        }
        else await page.EvaluateAsync("a => window.__rec.note(null, a[0], a[1])", new[] { title, body });

        var words = (title + " " + body).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        await page.WaitForTimeoutAsync(P(ms ?? NoteBaseMs + words * NoteMsPerWord));
        await page.EvaluateAsync("() => window.__rec.note(false)");
        await page.WaitForTimeoutAsync(P(AfterNoteMs));
    }

    /// <summary>A plain dark screen, before the first page: the video starts blank.</summary>
    public async Task BlankAsync(int ms = 1500)
    {
        await page.SetContentAsync("<html><body style='margin:0;background:#111827'></body></html>");
        await page.WaitForTimeoutAsync(P(ms));
    }

    /// <summary>A title over the blank screen that fades in, holds and fades out.</summary>
    public async Task BlankTitleAsync(string title, string subtitle, string tag, int ms = 5000)
    {
        var html = $$$"""
            <html><body style="margin:0;height:100vh;display:flex;flex-direction:column;align-items:center;justify-content:center;
              background:#111827;color:#fff;font-family:system-ui,sans-serif;text-align:center">
            <style>.f{opacity:0;animation:in .9s ease forwards}.f2{animation-delay:.5s}.f3{animation-delay:1s}
              @keyframes in{to{opacity:1}} body.out .f{transition:opacity .6s;opacity:0!important;animation:none}</style>
            <h1 class="f" style="font-size:64px;margin:0 0 14px">{{{Html(title)}}}</h1>
            <p class="f f2" style="font-size:26px;color:#cbd5e1;margin:0 0 28px">{{{Html(subtitle)}}}</p>
            <span class="f f3" style="font-size:18px;color:#111;background:#f59e0b;padding:6px 14px;border-radius:999px;font-weight:600">{{{Html(tag)}}}</span>
            </body></html>
            """;
        await page.SetContentAsync(html);
        await page.WaitForTimeoutAsync(P(ms));
        await page.EvaluateAsync("() => document.body.classList.add('out')");
        await page.WaitForTimeoutAsync(P(900));
    }

    private static string Html(string text) => System.Net.WebUtility.HtmlEncode(text);

    /// <summary>A full-screen card between sections.</summary>
    public async Task TitleCardAsync(string title, string subtitle, int ms = 3500)
    {
        await page.EvaluateAsync("([t, s]) => window.__rec.card(t, s)", new[] { title, subtitle });
        await page.WaitForTimeoutAsync(P(ms));
        await page.EvaluateAsync("() => window.__rec.card(null)");
        await page.WaitForTimeoutAsync(P(AfterCardMs));
    }

    /// <summary>Scrolls to an element, dims everything else, and outlines it for a while.</summary>
    public async Task SpotlightAsync(ILocator target, string? label = null, int ms = 2500)
    {
        await CenterAsync(target);
        await target.EvaluateAsync("(el, label) => window.__rec.spotlight(el, label)", label ?? "");
        await page.WaitForTimeoutAsync(P(ms));
        await page.EvaluateAsync("() => window.__rec.spotlight(null)");
        await page.WaitForTimeoutAsync(P(AfterSpotlightMs));
    }

    /// <summary>Glides the pointer to the element, outlines it briefly, and clicks it there.</summary>
    public async Task ClickAsync(ILocator target, string? label = null)
    {
        await CenterAsync(target);
        var box = await target.BoundingBoxAsync() ?? throw new InvalidOperationException("Element isn't visible.");
        var (x, y) = ((float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2));
        await GlideAsync(x, y);
        await target.EvaluateAsync("(el, label) => window.__rec.spotlight(el, label)", label ?? "");
        await page.WaitForTimeoutAsync(P(PreClickHighlightMs));
        await page.EvaluateAsync("() => window.__rec.spotlight(null)");
        await page.Mouse.ClickAsync(x, y);
        await page.WaitForTimeoutAsync(P(AfterClickMs));
    }

    /// <summary>Clicks into a field and types into it one key at a time, then leaves it (Blazor binds on change).</summary>
    public async Task TypeAsync(ILocator field, string text, int keyDelayMs = KeyDelayMs)
    {
        await ClickAsync(field);
        await field.FillAsync("");
        await field.PressSequentiallyAsync(text, new() { Delay = P(keyDelayMs) });
        await field.PressAsync("Tab");
        await page.WaitForTimeoutAsync(P(AfterTypingMs));
    }

    public Task PauseAsync(int ms) => page.WaitForTimeoutAsync(P(ms));

    /// <summary>Moves the pointer to a point over a set time, easing in and out, like a hand on a mouse.</summary>
    private async Task GlideAsync(float x, float y)
    {
        var distance = Math.Sqrt(Math.Pow(x - _x, 2) + Math.Pow(y - _y, 2));
        var duration = P((int)(GlideBaseMs + distance * GlideMsPerPx));
        var (fromX, fromY) = (_x, _y);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < duration)
        {
            var t = clock.ElapsedMilliseconds / (double)duration;
            var e = t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // ease in-out
            await page.Mouse.MoveAsync((float)(fromX + (x - fromX) * e), (float)(fromY + (y - fromY) * e));
            await page.WaitForTimeoutAsync(15);
        }
        await page.Mouse.MoveAsync(x, y);
        (_x, _y) = (x, y);
    }

    /// <summary>Scrolls the element to the middle of the window, so its spotlight and label are fully on screen.</summary>
    private async Task CenterAsync(ILocator target)
    {
        await target.EvaluateAsync("el => el.scrollIntoView({ block: el.offsetHeight > window.innerHeight - 120 ? 'start' : 'center', inline: 'nearest' })");
        await page.WaitForTimeoutAsync(P(ScrollSettleMs));
    }

    // ---------- Simulator ----------

    /// <summary>A phone card by its exact name ("Mike" doesn't match "Michael").</summary>
    public ILocator Phone(string name) =>
        page.Locator("article.phone", new() { Has = page.Locator(".phone-head strong", new() { HasTextRegex = new Regex($"^{Regex.Escape(name)}( \\(organizer\\))?$") }) });

    public ILocator PhoneButton(string phone, string word) =>
        Phone(phone).Locator(".quick").GetByRole(AriaRole.Button, new() { Name = word, Exact = true });

    /// <summary>A message on a phone containing the text; newest first (the thread is newest-first in the page).</summary>
    public ILocator Message(string phone, string containing) =>
        Phone(phone).Locator(".thread .msg", new() { HasText = containing }).First;

    /// <summary>
    /// Waits until a phone has more than <paramref name="after"/> messages containing the text, and returns
    /// the newest. Pass the count from before an action when an older matching message already exists.
    /// </summary>
    public async Task<ILocator> WaitForMessageAsync(string phone, string containing, int after = 0, int timeoutMs = 30000)
    {
        var started = DateTime.UtcNow;
        while (await CountMessagesAsync(phone, containing) <= after)
        {
            if ((DateTime.UtcNow - started).TotalMilliseconds > timeoutMs)
                throw new TimeoutException($"No new message containing '{containing}' on {phone}'s phone.");
            await page.WaitForTimeoutAsync(250); // the simulator refreshes every second
        }
        return Message(phone, containing);
    }

    public Task<int> CountMessagesAsync(string phone, string containing) =>
        Phone(phone).Locator(".thread .msg", new() { HasText = containing }).CountAsync();

    /// <summary>Waits until the simulator isn't busy and has nothing queued, for a couple of seconds in a row.</summary>
    public async Task WaitUntilQuietAsync(int quietMs = 2000, int timeoutMs = 120000)
    {
        var started = DateTime.UtcNow;
        DateTime? quietSince = null;
        await page.WaitForTimeoutAsync(500);
        while ((DateTime.UtcNow - started).TotalMilliseconds < timeoutMs)
        {
            var busy = (await page.Locator(".sim-status").AllInnerTextsAsync()).Any(s => s.StartsWith("Working"));
            var waitingText = await page.Locator(".sim-group span.muted", new() { HasTextRegex = new Regex(@"^\d+ waiting$") }).InnerTextAsync();
            var waiting = int.Parse(waitingText.Split(' ')[0]);

            if (busy || waiting > 0) quietSince = null;
            else if (quietSince is null) quietSince = DateTime.UtcNow;
            else if ((DateTime.UtcNow - quietSince.Value).TotalMilliseconds >= quietMs) return;

            await page.WaitForTimeoutAsync(400);
        }
        Console.Error.WriteLine("  (gave up waiting for the simulator to go quiet)");
    }

    /// <summary>Resets the simulator in a separate page that isn't recorded, so the video starts with empty phones.</summary>
    public async Task ResetOffCameraAsync()
    {
        await using var context = await browser.NewContextAsync();
        var sim = await context.NewPageAsync();
        await sim.GotoAsync(Url("/dev/simulator"));
        await sim.WaitForFunctionAsync("() => window.Blazor !== undefined");
        await sim.WaitForTimeoutAsync(1500);
        var reset = sim.GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true });
        await reset.ClickAsync();
        await sim.WaitForTimeoutAsync(500);
        for (var i = 0; i < 120 && !await reset.IsEnabledAsync(); i++) await sim.WaitForTimeoutAsync(500);
    }

    /// <summary>The newest login code on the organizer's phone, read in a separate page that isn't recorded.</summary>
    public async Task<string> ReadLoginCodeAsync()
    {
        await using var context = await browser.NewContextAsync();
        var sim = await context.NewPageAsync();
        await sim.GotoAsync(Url("/dev/simulator"));
        var thread = sim.Locator("article.phone.organizer .thread");
        for (var i = 0; i < 40; i++)
        {
            var match = Regex.Match(await thread.InnerTextAsync(), @"login code: (\d{6})"); // newest first
            if (match.Success) return match.Groups[1].Value;
            await sim.WaitForTimeoutAsync(500);
        }
        throw new InvalidOperationException("No login code arrived on the organizer phone.");
    }

    /// <summary>Shows a text as a phone notification on top of the current page.</summary>
    public async Task NotificationAsync(string from, string body, int ms = 3500)
    {
        await page.EvaluateAsync("([f, b]) => window.__rec.toast(f, b)", new[] { from, body });
        await page.WaitForTimeoutAsync(P(ms));
        await page.EvaluateAsync("() => window.__rec.toast(null)");
    }

    /// <summary>Installed on every page of the recording context.</summary>
    public const string OverlayScript = """
        (() => {
          if (window.__rec) return;
          const css = `
            #rec-pointer { position: fixed; width: 22px; height: 22px; margin: -11px 0 0 -11px; border-radius: 50%;
              background: rgba(245, 158, 11, .85); border: 2px solid #fff; box-shadow: 0 0 0 2px rgba(0,0,0,.35);
              z-index: 2147483647; pointer-events: none; }
            .rec-ripple { position: fixed; width: 22px; height: 22px; margin: -11px 0 0 -11px; border-radius: 50%;
              border: 3px solid rgba(245, 158, 11, .95); z-index: 2147483646; pointer-events: none;
              animation: rec-ripple .7s ease-out forwards; }
            @keyframes rec-ripple { to { transform: scale(3.2); opacity: 0; } }
            #rec-spot { position: fixed; border: 3px solid #f59e0b; border-radius: 8px; z-index: 2147483640;
              box-shadow: 0 0 0 9999px rgba(15, 23, 42, .45); pointer-events: none; transition: all .25s ease; }
            #rec-spot span { position: absolute; left: -3px; bottom: 100%; margin-bottom: 6px; background: #f59e0b; color: #111;
              font: 600 15px/1.3 system-ui, sans-serif; padding: 3px 8px; border-radius: 4px; white-space: nowrap; }
            #rec-caption { position: fixed; left: 0; right: 0; bottom: 0; z-index: 2147483645; pointer-events: none;
              background: rgba(17, 24, 39, .92); color: #fff; font: 18px/1.4 system-ui, sans-serif; padding: 12px 24px; min-height: 58px; }
            #rec-caption strong { font-size: 22px; display: block; }
            #rec-card { position: fixed; inset: 0; z-index: 2147483647; display: flex; flex-direction: column;
              align-items: center; justify-content: center; background: #111827; color: #fff; font-family: system-ui, sans-serif;
              text-align: center; opacity: 0; transition: opacity .4s ease; pointer-events: none; }
            #rec-card.on { opacity: 1; }
            #rec-card h1 { font-size: 54px; margin: 0 0 14px; } #rec-card p { font-size: 24px; color: #cbd5e1; margin: 0; max-width: 1100px; }
            #rec-toast { position: fixed; top: 24px; right: 24px; width: 380px; z-index: 2147483646; background: #fff;
              border-radius: 14px; box-shadow: 0 12px 40px rgba(0,0,0,.35); padding: 14px 16px; font: 16px/1.4 system-ui, sans-serif;
              color: #111; transform: translateY(-140%); transition: transform .35s ease; pointer-events: none; }
            #rec-note-dim { position: fixed; inset: 0; z-index: 2147483641; background: rgba(15, 23, 42, .55); pointer-events: none;
              opacity: 0; transition: opacity .3s ease; }
            #rec-note-dim.on { opacity: 1; }
            #rec-note-ring { position: fixed; inset: 0; z-index: 2147483642; pointer-events: none; }
            #rec-note-ring svg { position: absolute; inset: 0; width: 100%; height: 100%; }
            #rec-note-ring .ring { position: fixed; border: 3px solid #f59e0b; border-radius: 10px; }
            #rec-note { position: fixed; z-index: 2147483647; width: 440px; background: #fff; color: #111827; border-radius: 12px;
              border-top: 6px solid #f59e0b; box-shadow: 0 18px 50px rgba(0,0,0,.4); padding: 18px 22px 20px;
              font: 19px/1.45 system-ui, sans-serif; pointer-events: none; opacity: 0; transform: translateY(8px);
              transition: opacity .3s ease, transform .3s ease; }
            #rec-note.on { opacity: 1; transform: none; }
            #rec-note h3 { font-size: 24px; line-height: 1.25; margin: 0 0 8px; }
            #rec-note p { margin: 0; color: #334155; }
            #rec-badge { position: fixed; top: 12px; right: 14px; z-index: 2147483644; pointer-events: none;
              background: #111827; color: #fff; font: 600 14px/1 system-ui, sans-serif; padding: 7px 12px; border-radius: 999px;
              box-shadow: 0 2px 8px rgba(0,0,0,.25); }
            #rec-badge b { color: #f59e0b; }
            #rec-toast.on { transform: none; } #rec-toast b { display: block; font-size: 13px; color: #64748b; margin-bottom: 4px; }
          `;
          const ready = (fn) => document.body ? fn() : document.addEventListener('DOMContentLoaded', fn);
          let pointer, spot, caption, card, toast, badge, noteDim, noteRing, note;
          const pos = JSON.parse(sessionStorage.getItem('rec-pos') || '[800, 500]');

          ready(() => {
            const style = document.createElement('style'); style.id = 'rec-style'; style.textContent = css; document.head.appendChild(style);
            pointer = document.createElement('div'); pointer.id = 'rec-pointer';
            pointer.style.left = pos[0] + 'px'; pointer.style.top = pos[1] + 'px';
            document.body.appendChild(pointer);
            const last = sessionStorage.getItem('rec-caption');
            if (last) { const [t, d] = JSON.parse(last); window.__rec.caption(t, d); }
            const b = sessionStorage.getItem('rec-badge');
            if (b) window.__rec.badge(b);
          });

          // Blazor's enhanced navigation patches the page without reloading, which can drop
          // elements it didn't render. Put ours back if that happens.
          setInterval(() => {
            if (!document.body) return;
            for (const el of [pointer, caption, spot, card, toast, badge, noteDim, noteRing, note])
              if (el && !el.isConnected) document.body.appendChild(el);
            if (!document.getElementById('rec-style')) {
              const s = document.createElement('style'); s.id = 'rec-style'; s.textContent = css; document.head.appendChild(s);
            }
            const pad = caption ? '80px' : '';
            if (document.body.style.paddingBottom !== pad) document.body.style.paddingBottom = pad;
          }, 200);

          document.addEventListener('mousemove', (e) => {
            if (!pointer) return;
            pointer.style.left = e.clientX + 'px'; pointer.style.top = e.clientY + 'px';
            sessionStorage.setItem('rec-pos', JSON.stringify([e.clientX, e.clientY]));
          }, true);
          document.addEventListener('mousedown', (e) => {
            const r = document.createElement('div'); r.className = 'rec-ripple';
            r.style.left = e.clientX + 'px'; r.style.top = e.clientY + 'px';
            document.body.appendChild(r); setTimeout(() => r.remove(), 800);
          }, true);

          window.__rec = {
            caption(title, detail) {
              if (title === null) { sessionStorage.removeItem('rec-caption'); caption?.remove(); caption = null; return; }
              sessionStorage.setItem('rec-caption', JSON.stringify([title, detail || '']));
              if (!caption) { caption = document.createElement('div'); caption.id = 'rec-caption'; document.body.appendChild(caption); }
              caption.innerHTML = '';
              const t = document.createElement('strong'); t.textContent = title;
              const d = document.createElement('div'); d.textContent = detail || '';
              caption.append(t, d);
            },
            spotlight(el, label) {
              if (!el) { spot?.remove(); spot = null; return; }
              const r = el.getBoundingClientRect(), pad = 6;
              if (!spot) { spot = document.createElement('div'); spot.id = 'rec-spot'; document.body.appendChild(spot); }
              Object.assign(spot.style, { left: (r.left - pad) + 'px', top: (r.top - pad) + 'px',
                width: (r.width + pad * 2) + 'px', height: (r.height + pad * 2) + 'px' });
              spot.innerHTML = label ? '<span></span>' : '';
              if (label) {
                const tag = spot.firstChild;
                tag.textContent = label;
                // Keep the label on screen: below the box near the top, right-aligned near the right edge.
                if (r.top < 40) Object.assign(tag.style, { bottom: 'auto', top: '100%', marginBottom: '0', marginTop: '6px' });
                if (r.left + tag.offsetWidth > window.innerWidth - 12) Object.assign(tag.style, { left: 'auto', right: '-3px' });
              }
            },
            card(title, subtitle) {
              if (!card) { card = document.createElement('div'); card.id = 'rec-card'; document.body.appendChild(card); }
              if (title === null) { card.classList.remove('on'); return; }
              card.innerHTML = '<h1></h1><p></p>';
              card.firstChild.textContent = title; card.lastChild.textContent = subtitle || '';
              requestAnimationFrame(() => card.classList.add('on'));
            },
            badge(text) {
              sessionStorage.setItem('rec-badge', text);
              if (!badge) { badge = document.createElement('div'); badge.id = 'rec-badge'; document.body.appendChild(badge); }
              const [lead, ...rest] = text.split(' · ');
              badge.innerHTML = '<b></b>'; badge.firstChild.textContent = lead;
              if (rest.length) badge.append(' · ' + rest.join(' · '));
            },
            // A pause-and-explain note: dims the page, rings the element, and puts the note where there's room.
            note(el, title, body) {
              if (el === false) {
                note?.classList.remove('on'); noteDim?.classList.remove('on');
                const [n, r, d] = [note, noteRing, noteDim]; note = noteRing = noteDim = null;
                setTimeout(() => { n?.remove(); r?.remove(); d?.remove(); }, 300);
                return;
              }
              note = document.createElement('div'); note.id = 'rec-note';
              note.innerHTML = '<h3></h3>' + (body ? '<p></p>' : '');
              note.firstChild.textContent = title; if (body) note.lastChild.textContent = body;
              document.body.appendChild(note);
              const W = window.innerWidth, H = window.innerHeight, gap = 18, m = 14;
              let left, top;
              if (!el) {
                noteDim = document.createElement('div'); noteDim.id = 'rec-note-dim'; document.body.appendChild(noteDim);
                requestAnimationFrame(() => noteDim.classList.add('on'));
                note.style.width = '620px'; note.style.fontSize = '21px';
                left = (W - note.offsetWidth) / 2; top = (H - note.offsetHeight) / 2;
              } else {
                const w = note.offsetWidth, h = note.offsetHeight;
                const rects = [].concat(el).map(e => e.getBoundingClientRect());
                const r = { left: Math.min(...rects.map(x => x.left)), top: Math.min(...rects.map(x => x.top)),
                            right: Math.max(...rects.map(x => x.right)), bottom: Math.max(...rects.map(x => x.bottom)) };
                r.width = r.right - r.left; r.height = r.bottom - r.top;
                const pad = 6;
                // One dim layer with a hole and an outline per element, so two texts on distant phones
                // don't pull in everything between them.
                noteRing = document.createElement('div'); noteRing.id = 'rec-note-ring';
                const holes = rects.map(x => `<rect x="${x.left - pad}" y="${x.top - pad}" width="${x.width + pad * 2}" height="${x.height + pad * 2}" rx="10" fill="black"/>`).join('');
                noteRing.innerHTML = `<svg><defs><mask id="rec-holes"><rect width="100%" height="100%" fill="white"/>${holes}</mask></defs>`
                  + `<rect width="100%" height="100%" fill="rgba(15, 23, 42, .55)" mask="url(#rec-holes)"/></svg>`;
                for (const x of rects) {
                  const ring = document.createElement('div'); ring.className = 'ring';
                  Object.assign(ring.style, { left: (x.left - pad) + 'px', top: (x.top - pad) + 'px',
                    width: (x.width + pad * 2) + 'px', height: (x.height + pad * 2) + 'px' });
                  noteRing.appendChild(ring);
                }
                document.body.appendChild(noteRing);
                const clampY = (y) => Math.max(m, Math.min(H - h - m, y));
                const clampX = (x) => Math.max(m, Math.min(W - w - m, x));
                if (r.right + pad + gap + w <= W - m) { left = r.right + pad + gap; top = clampY(r.top); }
                else if (r.left - pad - gap - w >= m) { left = r.left - pad - gap - w; top = clampY(r.top); }
                else if (r.bottom + pad + gap + h <= H - m) { top = r.bottom + pad + gap; left = clampX(r.left); }
                else if (r.top - pad - gap - h >= m) { top = r.top - pad - gap - h; left = clampX(r.left); }
                else { left = W - w - m * 2; top = H - h - m * 2; } // big target: inside its bottom-right corner
              }
              Object.assign(note.style, { left: left + 'px', top: top + 'px' });
              requestAnimationFrame(() => note.classList.add('on'));
            },
            toast(from, body) {
              if (!toast) { toast = document.createElement('div'); toast.id = 'rec-toast'; document.body.appendChild(toast); }
              if (from === null) { toast.classList.remove('on'); return; }
              toast.innerHTML = '<b></b><div></div>';
              toast.firstChild.textContent = from; toast.lastChild.textContent = body;
              requestAnimationFrame(() => toast.classList.add('on'));
            },
          };
        })();
        """;
}

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// The dashboard's one self-contained stylesheet (§12: one static CSS file, no
/// build chain). Served as a const from <c>/dashboard/dashboard.css</c> so the host
/// stays a single deployable with no wwwroot or static-files middleware to wire.
/// Nocturne tokens for the fleet board, with the older table views remapped
/// onto the same dark ground so the operator surface is one product.
/// </summary>
internal static class DashboardCss
{
    public const string ContentType = "text/css; charset=utf-8";

    public const string Content = """
    @import url('https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap');
    :root {
      --color-bg: #161826;
      --color-surface: #232532;
      --color-text: #e9e9ed;
      --color-accent: #9184d9;
      --color-accent-2: #a7a1db;
      --color-divider: color-mix(in srgb, #e9e9ed 16%, transparent);
      --color-neutral-100: #f3f5fe; --color-neutral-200: #e4e7f5; --color-neutral-300: #cfd3e5;
      --color-neutral-400: #b2b6ca; --color-neutral-500: #9397ab; --color-neutral-600: #75798c;
      --color-neutral-700: #595d6c; --color-neutral-800: #3f424d; --color-neutral-900: #292b31;
      --color-accent-100: #f5f4ff; --color-accent-200: #e7e5fe; --color-accent-300: #d2cefd;
      --color-accent-400: #b5abfc; --color-accent-500: #968ae0; --color-accent-600: #796cbf;
      --color-accent-700: #5d5294; --color-accent-800: #423a6a; --color-accent-900: #2b2741;
      --font-heading: "Inter", system-ui, sans-serif;
      --font-body: "Inter", system-ui, sans-serif;
      --font-mono: "JetBrains Mono", ui-monospace, Menlo, monospace;
      --radius-sm: 4px; --radius-md: 8px; --radius-lg: 14px;
      --state-live: #79c39b; --state-wait: #e2b06a; --state-error: #e07a72; --forward: #8ab6c9;
      --forward-line: color-mix(in srgb, var(--forward) 45%, var(--color-bg));
      --forward-tint: color-mix(in srgb, var(--forward) 10%, transparent);
      --accent-tint: color-mix(in srgb, var(--color-accent) 10%, transparent);
      --accent-tint-hi: color-mix(in srgb, var(--color-accent) 20%, transparent);
      --surface-1: color-mix(in srgb, var(--color-bg) 78%, var(--color-neutral-900));
      --surface-2: color-mix(in srgb, var(--color-bg) 55%, var(--color-neutral-900));
      --hairline: color-mix(in srgb, var(--color-neutral-900) 65%, var(--color-bg));
      --well: color-mix(in srgb, var(--color-bg) 82%, #000000);
      --bg: var(--color-bg); --panel: var(--color-surface); --ink: var(--color-text);
      --muted: var(--color-neutral-600); --line: var(--color-neutral-800);
      --accent: var(--color-accent-500); --accent-ink: var(--color-text);
      --ok: var(--state-live); --ok-bg: color-mix(in srgb, var(--state-live) 16%, var(--color-bg));
      --warn: var(--state-wait); --warn-bg: color-mix(in srgb, var(--state-wait) 16%, var(--color-bg));
      --bad: var(--state-error); --bad-bg: color-mix(in srgb, var(--state-error) 16%, var(--color-bg));
      --idle: var(--color-neutral-600); --idle-bg: var(--surface-2);
    }
    * , *::before, *::after { box-sizing: border-box; }
    html, body { height: 100%; }
    body {
      margin: 0; background: var(--color-bg); color: var(--color-text);
      font-family: var(--font-body); -webkit-font-smoothing: antialiased;
      display: flex; flex-direction: column; min-height: 100%;
    }
    nav {
      display: flex; align-items: center; gap: 4px; padding: 0 16px;
      background: var(--panel); border-bottom: 1px solid var(--line);
      position: sticky; top: 0; z-index: 1;
    }
    nav .brand { font-weight: 700; margin-right: 16px; padding: 12px 0; letter-spacing: .3px; }
    nav a {
      padding: 12px 12px; color: var(--muted); text-decoration: none;
      border-bottom: 2px solid transparent;
    }
    nav a:hover { color: var(--ink); }
    nav a.active { color: var(--ink); border-bottom-color: var(--accent); font-weight: 600; }
    nav .logout { margin-left: auto; }
    nav .logout button {
      background: none; border: 1px solid var(--line); color: var(--muted);
      border-radius: 6px; padding: 5px 10px; cursor: pointer; font: inherit;
    }
    nav .logout button:hover { color: var(--ink); }
    main { padding: 20px; max-width: 1200px; margin: 0 auto; width: 100%; flex: 1; }
    .window-bar {
      display: flex; align-items: center; justify-content: center; gap: 12px;
      padding: 6px 16px; flex: none;
      border-top: 1px solid var(--line); background: var(--panel);
      font: 400 11px var(--font-mono); color: var(--muted);
    }
    .window-bar__label { letter-spacing: .08em; text-transform: uppercase; font-size: 10px; }
    .window-bar a { color: var(--muted); padding: 2px 0; border-bottom: none; }
    .window-bar a:hover { color: var(--ink); }
    .window-bar a.is-on { color: var(--ink); font-weight: 600; }
    h1 { font-size: 20px; margin: 0 0 4px; }
    h2 { font-size: 15px; margin: 24px 0 8px; }
    .sub { color: var(--muted); margin: 0 0 16px; }
    section, .card {
      background: var(--panel); border: 1px solid var(--line);
      border-radius: 10px; padding: 14px 16px; margin-bottom: 16px;
    }
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--line); vertical-align: top; }
    th { color: var(--muted); font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: .04em; }
    tr:last-child td { border-bottom: none; }
    td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
    code, .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 12px; }
    a { color: var(--accent); }
    .badge {
      display: inline-block; padding: 2px 8px; border-radius: 999px;
      font-size: 12px; font-weight: 600; background: var(--idle-bg); color: var(--idle);
    }
    .state-working, .ready { background: var(--ok-bg); color: var(--ok); }
    .state-submitted { background: var(--idle-bg); color: var(--idle); }
    .state-verifying { background: var(--warn-bg); color: var(--warn); }
    .state-blockedoninput, .state-parked, .backpressure { background: var(--warn-bg); color: var(--warn); }
    .state-completed { background: var(--ok-bg); color: var(--ok); }
    .state-failed, .state-rejected, .state-canceled, .down { background: var(--bad-bg); color: var(--bad); }
    .pill-row { display: flex; flex-wrap: wrap; gap: 6px; }
    .empty { color: var(--muted); font-style: italic; margin: 8px 0; }
    .nt { color: var(--muted); font-size: 12px; }
    .metrics { display: flex; flex-wrap: wrap; gap: 20px; }
    .metric { min-width: 90px; }
    .metric .n { font-size: 22px; font-weight: 700; font-variant-numeric: tabular-nums; }
    .metric .l { color: var(--muted); font-size: 12px; }
    .idle-row td { opacity: .62; }
    .machine-tasks { margin: 6px 0 0; padding-left: 0; list-style: none; }
    .machine-tasks li { padding: 3px 0; border-top: 1px dashed var(--line); }
    .subtree { color: var(--muted); font-size: 12px; margin-top: 6px; }
    .parks-hot { color: var(--bad); font-weight: 700; }
    /* Measured-usage section (§10, §12): the harness's own numbers, NOT the plane's.
       The visual separation is load-bearing, not decorative (§2 principle 2) — a reader has to
       be able to tell a worker's claim about itself from something the plane observed, and the
       banner alone would not survive a skim. A dashed left rule plus a tinted ground marks the
       whole block as a different KIND of fact, and the tag in the heading names it outright. */
    section.measured {
      border-left: 3px dashed var(--idle); padding-left: 14px;
      background: linear-gradient(90deg, var(--idle-bg) 0%, transparent 40%);
    }
    section.measured > h2 { display: flex; align-items: baseline; gap: 8px; }
    .measured-tag {
      font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.06em;
      color: var(--idle); background: var(--idle-bg); padding: 2px 7px; border-radius: 10px;
    }
    section.measured tfoot td { border-top: 2px solid var(--line); font-weight: 600; }
    /* Permission allow/deny form (§11 permission bridge, human-only) */
    .permission-decide input[type=text] {
      width: 200px; padding: 6px 8px; border: 1px solid var(--line);
      border-radius: 6px; background: var(--bg); color: var(--ink); font: inherit;
    }
    .permission-decide button {
      padding: 6px 12px; margin-left: 6px; border: none; border-radius: 6px;
      background: var(--accent); color: var(--accent-ink); font: inherit; font-weight: 600; cursor: pointer;
    }
    .permission-decide button[value=deny] { background: var(--bad); }
    .permission-decide .nt { margin-top: 6px; }
    /* Revoke machine (§13, human-only). Destructive, so it reads as such rather than
       sitting in the accent colour every other action shares. */
    .machine-revoke { margin-top: 14px; padding-top: 12px; border-top: 1px solid var(--line); }
    .machine-revoke button {
      padding: 6px 12px; border: none; border-radius: 6px;
      background: var(--bad); color: var(--accent-ink); font: inherit; font-weight: 600; cursor: pointer;
    }
    /* Login */
    .conformance-start label { display: block; font-weight: 600; margin-bottom: 6px; }
    .conformance-start input[type=text] {
      width: 100%; max-width: 280px; padding: 8px 10px; border: 1px solid var(--line);
      border-radius: 6px; background: var(--bg); color: var(--ink); font: inherit;
    }
    .conformance-start button {
      margin-top: 12px; padding: 6px 12px; border: none; border-radius: 6px;
      background: var(--accent); color: var(--accent-ink); font: inherit; font-weight: 600; cursor: pointer;
    }
    .login-wrap { max-width: 420px; margin: 8vh auto; }
    .login-wrap input[type=text], .login-wrap input[type=password] {
      width: 100%; padding: 10px; border: 1px solid var(--line);
      border-radius: 8px; background: var(--bg); color: var(--ink); font: inherit;
    }
    .login-wrap button {
      margin-top: 10px; width: 100%; padding: 10px; border: none; border-radius: 8px;
      background: var(--accent); color: var(--accent-ink); font: inherit; font-weight: 600; cursor: pointer;
    }
    .login-wrap .or { display: block; color: var(--muted); font-size: 12px; margin: 12px 0 4px; }
    .login-wrap .err { color: var(--bad); margin: 8px 0; }
    .login-wrap .seam { color: var(--muted); font-size: 12px; margin-top: 14px; }
    /* Connect / how-to */
    .howto ol { padding-left: 1.3em; margin: 8px 0; }
    .howto li { margin: 8px 0; }
    .howto pre, pre.howto, pre.report {
      background: var(--bg); border: 1px solid var(--line); border-radius: 8px;
      padding: 10px 12px; overflow-x: auto; font: 12px/1.45 ui-monospace, SFMono-Regular, Menlo, monospace;
      white-space: pre-wrap;
    }
    .secret {
      background: var(--warn-bg); border: 1px solid var(--warn);
      border-radius: 8px; padding: 12px; margin: 10px 0;
    }
    .secret label { display: block; font-weight: 600; margin-bottom: 6px; }
    .secret input[type=text] {
      width: 100%; padding: 8px 10px; border: 1px solid var(--line);
      border-radius: 6px; background: var(--bg); color: var(--ink);
      font: 12px/1.4 ui-monospace, SFMono-Regular, Menlo, monospace;
    }
    .connect-claim label, .connect-enroll label { display: block; font-weight: 600; margin: 10px 0 6px; }
    .connect-claim input[type=text] {
      width: 100%; max-width: 360px; padding: 8px 10px; border: 1px solid var(--line);
      border-radius: 6px; background: var(--bg); color: var(--ink); font: inherit;
    }
    .connect-claim button, .connect-enroll button {
      margin-top: 12px; padding: 6px 12px; border: none; border-radius: 6px;
      background: var(--accent); color: var(--accent-ink); font: inherit; font-weight: 600; cursor: pointer;
    }
    .connect-claim .check { font-weight: 400; display: flex; align-items: center; gap: 8px; }

    /* ── fleet board (1a lane board) ── */
    a { color: var(--color-accent-400); text-decoration: none; }
    a:hover { color: var(--color-accent-300); }
    @keyframes lbpulse { 0% { transform: scale(1); opacity: .55; } 70% { transform: scale(2.6); opacity: 0; } 100% { transform: scale(2.6); opacity: 0; } }
    @keyframes lbbreathe { 0%, 100% { opacity: .95; } 50% { opacity: .45; } }
    @keyframes lbcursor { 0%, 49% { opacity: 1; } 50%, 100% { opacity: 0; } }

    .obs-page { min-height: 100vh; height: 100vh; display: flex; overflow: hidden; }
    .obs-board { width: 100%; height: 100%; background: var(--color-bg); overflow: hidden; display: flex; flex-direction: column; }

    .obs-header {
      display: flex; align-items: center; gap: 14px;
      padding: 0 16px; height: 52px; flex: none;
      border-bottom: 1px solid var(--color-neutral-900);
      background: var(--surface-1);
    }
    .obs-header__brand { display: flex; align-items: baseline; gap: 8px; }
    .obs-header__brand-name { font: 600 13px var(--font-body); letter-spacing: .02em; }
    .obs-header__brand-sub { font: 400 11px var(--font-mono); color: var(--color-neutral-600); }
    .obs-header__rule { width: 1px; height: 22px; background: var(--color-neutral-900); }
    .obs-header__counts { display: flex; gap: 14px; align-items: center; }
    .obs-count { display: flex; align-items: center; gap: 6px; }
    .obs-count__dot { width: 7px; height: 7px; border-radius: 50%; }
    .obs-count__label { font: 500 11px var(--font-mono); color: var(--color-neutral-300); }
    .obs-count__label--dim { color: var(--color-neutral-600); }
    .obs-header__spacer { flex: 1; }
    .obs-header__stats { display: flex; align-items: center; gap: 8px; font: 400 11px var(--font-mono); color: var(--color-neutral-600); }
    .obs-header__stats-sep { color: var(--color-neutral-800); }
    .obs-header__nav { display: flex; align-items: center; gap: 2px; }
    .obs-header__nav a {
      padding: 4px 7px; font: 500 11px var(--font-body); color: var(--color-neutral-600);
      border-bottom: none;
    }
    .obs-header__nav a:hover { color: var(--color-neutral-300); }
    .obs-header__logout { margin: 0; }
    .obs-header__logout button {
      background: none; border: 1px solid var(--color-neutral-800); color: var(--color-neutral-600);
      border-radius: var(--radius-sm); padding: 4px 8px; cursor: pointer;
      font: 500 11px var(--font-body);
    }
    .obs-header__logout button:hover { color: var(--color-neutral-300); }
    .obs-live-pill { display: flex; align-items: center; gap: 6px; padding: 5px 10px; border: 1px solid var(--color-accent-700); border-radius: var(--radius-sm); background: var(--accent-tint); }
    .obs-live-pill__dot { width: 6px; height: 6px; border-radius: 50%; background: var(--color-accent-400); animation: lbbreathe 1.6s ease-in-out infinite; }
    .obs-live-pill__label { font: 500 11px var(--font-body); color: var(--color-accent-300); }

    .obs-body { flex: 1; display: grid; grid-template-columns: 224px minmax(0,1fr) minmax(0,364px); min-height: 0; min-width: 0; }
    .obs-body--no-detail { grid-template-columns: 224px minmax(0,1fr); }
    .obs-body > * { min-width: 0; }

    .obs-rail { border-right: 1px solid var(--color-neutral-900); background: var(--surface-1); display: flex; flex-direction: column; min-height: 0; }
    .obs-rail__title { padding: 14px 14px 10px; font: 600 10px var(--font-body); letter-spacing: .10em; text-transform: uppercase; color: var(--color-neutral-600); flex: none; }
    .obs-rail__list { display: flex; flex-direction: column; gap: 2px; padding: 0 10px 14px; overflow: auto; }
    .obs-rail__list--teams { flex: 0 1 auto; max-height: 28%; }
    .obs-rail__list--ports { flex: 0 1 auto; max-height: 36%; gap: 6px; }
    .obs-rail__list--machines { flex: 1; }
    .obs-ports__empty, .obs-ports__bound { font: 400 10px var(--font-mono); color: var(--color-neutral-600); padding: 2px 6px; }
    .obs-ports__bound.is-off { color: var(--color-neutral-700); }
    .obs-ports__bind { display: flex; align-items: center; gap: 8px; padding: 0 6px; }
    .obs-ports__bind form { margin: 0; }
    .obs-ports__bind button, .obs-ports__form button, .obs-ports__preview button {
      background: none; border: 1px solid var(--color-neutral-800); color: var(--color-neutral-500);
      border-radius: var(--radius-sm); padding: 2px 6px; cursor: pointer; font: 500 9px var(--font-body);
    }
    .obs-ports__bind button:hover, .obs-ports__form button:hover, .obs-ports__preview button:hover { color: var(--color-neutral-200); }
    .obs-ports__form { display: flex; flex-wrap: wrap; gap: 4px; padding: 0 6px; }
    .obs-ports__form select {
      flex: 1; min-width: 0; background: var(--color-bg); color: var(--color-neutral-300);
      border: 1px solid var(--color-neutral-800); border-radius: var(--radius-sm);
      font: 400 10px var(--font-mono); padding: 2px 4px;
    }
    .obs-ports__label { padding: 6px 6px 0; font: 600 9px var(--font-body); letter-spacing: .08em; text-transform: uppercase; color: var(--color-neutral-700); }
    .obs-ports__preview { display: flex; align-items: center; gap: 6px; padding: 2px 6px; font: 400 10px var(--font-mono); color: var(--color-neutral-400); }
    .obs-ports__preview form { margin-left: auto; }
    .obs-team {
      display: flex; align-items: center; gap: 8px; padding: 6px 6px; border-radius: var(--radius-sm);
      color: var(--color-neutral-300); border-bottom: none;
    }
    .obs-team:hover { background: var(--surface-2); color: var(--color-neutral-200); }
    .obs-team.is-on { background: var(--surface-2); box-shadow: inset 2px 0 0 var(--color-accent-500); color: var(--color-neutral-200); }
    .obs-team__id { font: 400 10.5px var(--font-mono); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .obs-team__spacer { flex: 1; }
    .obs-team__n { font: 400 10px var(--font-mono); color: var(--color-neutral-600); flex: none; }
    .obs-machine { display: flex; align-items: center; gap: 8px; padding: 6px 6px; border-radius: var(--radius-sm); }
    .obs-machine__dot { width: 6px; height: 6px; border-radius: 50%; flex: none; }
    .obs-machine__id { font: 400 10.5px var(--font-mono); color: var(--color-neutral-300); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .obs-machine__spacer { flex: 1; }
    .obs-machine__load { font: 400 10px var(--font-mono); color: var(--color-neutral-600); white-space: nowrap; flex: none; }
    .obs-machine__revoke { margin: 0; }
    .obs-machine__revoke button {
      background: none; border: 0; padding: 0; cursor: pointer;
      font: 400 9px var(--font-mono); color: var(--color-neutral-700);
    }
    .obs-machine__revoke button:hover { color: var(--state-error); }
    .obs-rail__fill { flex: 1; }
    .obs-rail__footnote { padding: 12px 14px; border-top: 1px solid var(--color-neutral-900); font: 400 10px/1.6 var(--font-mono); color: var(--color-neutral-700); }

    .obs-lanes { display: flex; flex-direction: column; min-height: 0; min-width: 0; background: var(--color-bg); }
    .obs-lanes__head, .obs-row {
      display: grid;
      grid-template-columns: minmax(0,214px) minmax(0,214px) minmax(120px,1fr) minmax(0,148px) minmax(0,118px) 50px;
      gap: 12px;
    }
    .obs-lanes__head > *, .obs-row > * { min-width: 0; overflow: hidden; }
    .obs-lanes__head {
      padding: 9px 16px; border-bottom: 1px solid var(--color-neutral-900);
      font: 600 9.5px var(--font-body); letter-spacing: .09em; text-transform: uppercase; color: var(--color-neutral-700);
      flex: none; overflow: hidden;
    }
    .obs-lanes__head-usage { display: flex; align-items: center; gap: 7px; white-space: nowrap; overflow: hidden; }
    .obs-lanes__head-window { display: flex; justify-content: space-between; gap: 8px; min-width: 0; overflow: hidden; }
    .obs-lanes__head-window span:first-child { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .obs-lanes__head-window span:last-child { color: var(--color-neutral-800); flex: none; }
    .obs-usage-key { display: inline-flex; align-items: center; gap: 3px; text-transform: none; letter-spacing: 0; font: 400 9px var(--font-mono); color: var(--color-neutral-700); }
    .obs-usage-key__swatch { width: 6px; height: 3px; border-radius: 1px; }
    .obs-lanes__body { flex: 1; overflow-y: auto; }
    .obs-empty { padding: 24px 16px; font: 400 12px var(--font-body); color: var(--color-neutral-600); }

    .obs-row {
      align-items: center; padding: 0 16px; height: 46px;
      border-bottom: 1px solid var(--hairline); cursor: pointer; background: transparent;
      overflow: hidden;
    }
    .obs-row:hover { background: var(--surface-2); }
    .obs-row--selected { background: var(--surface-2); box-shadow: inset 2px 0 0 var(--color-accent-500); }
    .obs-row__identity { display: flex; align-items: center; gap: 9px; min-width: 0; }
    .obs-dot { position: relative; width: 9px; height: 9px; flex: none; display: block; margin-left: auto; }
    .obs-dot__pulse { position: absolute; inset: 0; border-radius: 50%; animation: lbpulse 2.2s ease-out infinite; }
    .obs-dot__core { position: absolute; inset: 1px; border-radius: 50%; }
    .obs-row__names { display: flex; flex-direction: column; gap: 2px; min-width: 0; flex: 1; }
    .obs-row__ns { font: 500 11.5px var(--font-mono); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .obs-row__sub { font: 400 9.5px var(--font-mono); color: var(--color-neutral-700); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .obs-row__status { display: flex; flex-direction: column; gap: 3px; min-width: 0; overflow: hidden; }
    .obs-row__state-line { display: flex; align-items: center; gap: 6px; }
    .obs-row__state-label { font: 500 9px var(--font-body); letter-spacing: .07em; text-transform: uppercase; }
    .obs-row__elapsed { font: 400 9.5px var(--font-mono); color: var(--color-neutral-700); }
    .obs-row__now { font: 400 11px var(--font-body); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

    .obs-track { position: relative; height: 30px; border-left: 1px solid var(--hairline); overflow: hidden; }
    .obs-track__line { position: absolute; left: 0; right: 0; top: 50%; height: 1px; background: var(--color-surface); }
    .obs-track__edge { position: absolute; right: 0; top: 2px; bottom: 2px; width: 1px; opacity: .8; }
    .obs-mark { position: absolute; top: 50%; }
    .obs-mark--tool { width: 2px; height: 9px; border-radius: 1px; transform: translate(-50%,-50%); background: var(--color-neutral-700); }
    .obs-mark--ask { width: 0; height: 0; border-left: 4px solid transparent; border-right: 4px solid transparent; border-bottom: 7px solid var(--color-accent-400); transform: translate(-50%,-100%); margin-top: -3px; }
    .obs-mark--answer { width: 0; height: 0; border-left: 4px solid transparent; border-right: 4px solid transparent; border-top: 7px solid var(--color-accent-600); transform: translate(-50%,0); margin-top: 3px; }
    .obs-mark--forward { height: 3px; border-radius: 2px; background: var(--forward); opacity: .85; transform: translateY(7px); }
    .obs-mark--error { width: 7px; height: 7px; background: var(--state-error); transform: translate(-50%,-50%) rotate(45deg); }
    .obs-mark--park { width: 8px; height: 8px; border-radius: 50%; border: 1px solid var(--state-wait); transform: translate(-50%,-50%); }
    .obs-mark--dispatch { top: 1px; bottom: 1px; width: 1px; background: var(--color-neutral-700); opacity: .7; }
    .obs-mark--done { width: 8px; height: 8px; border-radius: 50%; background: var(--color-neutral-500); transform: translate(-50%,-50%); }

    .obs-usage { display: flex; flex-direction: column; gap: 4px; min-width: 0; overflow: hidden; }
    .obs-usage__top { display: flex; align-items: baseline; gap: 6px; }
    .obs-usage__tokens { font: 500 10.5px var(--font-mono); }
    .obs-usage__cost { font: 400 9.5px var(--font-mono); color: var(--color-neutral-600); }
    .obs-usage__bar { display: flex; height: 3px; border-radius: 2px; overflow: hidden; background: var(--color-surface); }
    .obs-usage__rate { font: 400 9px var(--font-mono); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

    .obs-ports { display: flex; flex-wrap: nowrap; gap: 4px; min-width: 0; overflow: hidden; }
    .obs-port { display: inline-flex; align-items: center; gap: 5px; padding: 2px 6px; border: 1px solid; border-radius: var(--radius-sm); font: 400 9.5px var(--font-mono); flex: none; white-space: nowrap; }
    .obs-port__dot { width: 4px; height: 4px; border-radius: 50%; }
    .obs-row__trail { display: flex; justify-content: flex-end; align-items: center; gap: 6px; overflow: hidden; }
    .obs-unread { display: inline-flex; align-items: center; justify-content: center; min-width: 16px; height: 16px; padding: 0 4px; border-radius: var(--radius-md); background: var(--color-accent-900); color: var(--color-accent-300); font: 500 9.5px var(--font-mono); }
    .obs-attempt { font: 400 9.5px var(--font-mono); color: var(--color-neutral-700); white-space: nowrap; }

    .obs-detail { border-left: 1px solid var(--color-neutral-900); background: var(--surface-1); display: flex; flex-direction: column; min-height: 0; }
    .obs-detail__head { padding: 13px 14px; border-bottom: 1px solid var(--color-neutral-900); flex: none; }
    .obs-detail__title { display: flex; align-items: center; gap: 8px; }
    .obs-detail__title-dot { width: 7px; height: 7px; border-radius: 50%; flex: none; }
    .obs-detail__title-ns { font: 500 12px var(--font-mono); color: var(--color-text); }
    .obs-detail__meta { margin-top: 6px; font: 400 10.5px/1.6 var(--font-mono); color: var(--color-neutral-600); }
    .obs-detail__actions { display: flex; gap: 6px; margin-top: 10px; }
    .obs-btn { padding: 5px 10px; border-radius: var(--radius-sm); font: 500 10.5px var(--font-body); cursor: pointer; background: transparent; display: inline-flex; align-items: center; }
    .obs-btn--primary { border: 1px solid var(--color-accent-700); background: var(--accent-tint); color: var(--color-accent-300); }
    .obs-btn--primary:hover { background: var(--accent-tint-hi); color: var(--color-accent-200); }
    .obs-btn--ghost { border: 1px solid var(--color-neutral-800); color: var(--color-neutral-500); }
    .obs-btn--ghost:hover { border-color: var(--color-neutral-700); color: var(--color-neutral-300); }
    .obs-detail__section-title { padding: 11px 14px 7px; font: 600 9.5px var(--font-body); letter-spacing: .10em; text-transform: uppercase; color: var(--color-neutral-600); }
    .obs-detail__tabs { display: flex; gap: 2px; padding: 0 10px; flex: none; border-bottom: 1px solid var(--color-neutral-900); }
    .obs-detail__tab {
      background: none; border: 0; border-bottom: 2px solid transparent; margin-bottom: -1px;
      padding: 8px 8px 7px; cursor: pointer; color: var(--color-neutral-600);
      font: 500 10.5px var(--font-body); display: inline-flex; align-items: center; gap: 5px;
    }
    .obs-detail__tab:hover { color: var(--color-neutral-300); }
    .obs-detail__tab.is-on { color: var(--color-neutral-200); border-bottom-color: var(--color-accent-500); }
    .obs-detail__tab-n {
      min-width: 14px; height: 14px; padding: 0 4px; border-radius: 7px;
      background: var(--color-neutral-800); color: var(--color-neutral-400);
      font: 500 9px var(--font-mono); display: inline-flex; align-items: center; justify-content: center;
    }
    .obs-detail__tab.is-on .obs-detail__tab-n { background: var(--color-accent-900); color: var(--color-accent-300); }
    .obs-detail__panel { flex: 1; min-height: 0; display: flex; flex-direction: column; overflow: hidden; }
    .obs-exchange { display: flex; flex-direction: column; gap: 7px; padding: 12px 14px; overflow-y: auto; flex: 1; }
    .obs-message { display: flex; flex-direction: column; gap: 3px; padding: 8px 10px; border-radius: var(--radius-sm); border-left: 2px solid; }
    .obs-message__head { display: flex; align-items: center; gap: 6px; }
    .obs-message__from { font: 500 9px var(--font-body); letter-spacing: .07em; text-transform: uppercase; }
    .obs-message__spacer { flex: 1; }
    .obs-message__at { font: 400 9.5px var(--font-mono); color: var(--color-neutral-700); }
    .obs-message__text { font: 400 11px/1.5 var(--font-body); color: var(--color-neutral-300); text-wrap: pretty; }
    .obs-transcript-head { display: flex; align-items: center; gap: 8px; padding: 9px 14px 7px; flex: none; }
    .obs-transcript-head__title { font: 600 9.5px var(--font-body); letter-spacing: .10em; text-transform: uppercase; color: var(--color-neutral-600); }
    .obs-transcript-head__dot { width: 5px; height: 5px; border-radius: 50%; background: var(--state-live); animation: lbbreathe 1.4s ease-in-out infinite; }
    .obs-transcript-head__spacer { flex: 1; }
    .obs-transcript-head__tag { font: 400 9.5px var(--font-mono); color: var(--color-neutral-700); }
    .obs-transcript {
      flex: 1; overflow-y: auto; margin: 0 10px 12px; padding: 10px 11px;
      border: 1px solid var(--color-surface); border-radius: var(--radius-sm); background: var(--well);
    }
    .obs-transcript__line { display: flex; gap: 8px; padding: 1.5px 0; }
    .obs-transcript__time { font: 400 9.5px var(--font-mono); color: var(--color-neutral-800); flex: none; }
    .obs-transcript__text { font: 400 10px/1.45 var(--font-mono); text-wrap: pretty; }
    .obs-transcript__cursor { display: inline-block; width: 6px; height: 12px; background: var(--state-live); animation: lbcursor 1.05s step-end infinite; }
    .obs-forwards { flex: 1; overflow-y: auto; padding: 12px 14px 16px; display: flex; flex-direction: column; gap: 6px; }
    .obs-forwards__title { font: 600 9.5px var(--font-body); letter-spacing: .10em; text-transform: uppercase; color: var(--color-neutral-600); margin: 8px 0 4px; }
    .obs-forwards__title:first-child { margin-top: 0; }
    .obs-forwards__empty { font: 400 11px var(--font-body); color: var(--color-neutral-700); padding: 4px 0 8px; }
    .obs-forward { display: flex; align-items: center; gap: 8px; padding: 8px 10px; border: 1px solid var(--color-neutral-800); border-radius: var(--radius-sm); background: var(--surface-2); }
    .obs-forward.is-live { border-color: var(--forward-line); background: var(--forward-tint); }
    .obs-forward.is-preview { border-color: var(--color-accent-800); background: var(--accent-tint); }
    .obs-forward__name { font: 500 11px var(--font-mono); color: var(--color-neutral-200); }
    .obs-forward__meta { margin-left: auto; font: 400 10px var(--font-mono); color: var(--color-neutral-600); }
    .obs-board > .window-bar {
      background: var(--surface-1); border-top-color: var(--color-neutral-900);
      height: 28px; padding: 0 16px;
    }
    """;
}

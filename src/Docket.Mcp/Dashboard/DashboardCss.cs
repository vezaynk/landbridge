namespace Docket.Mcp.Dashboard;

/// <summary>
/// The dashboard's one self-contained stylesheet (§12: one static CSS file, no
/// build chain). Served as a const from <c>/dashboard/dashboard.css</c> so the host
/// stays a single deployable with no wwwroot or static-files middleware to wire.
/// Deliberately restrained — tables, state pills, a scannable grid — not styled to
/// death. Works in light and dark via a <c>prefers-color-scheme</c> block.
/// </summary>
internal static class DashboardCss
{
    public const string ContentType = "text/css; charset=utf-8";

    public const string Content = """
    :root {
      --bg: #f7f8fa; --panel: #ffffff; --ink: #1b1f24; --muted: #6b7280;
      --line: #e5e7eb; --accent: #2563eb; --accent-ink: #ffffff;
      --ok: #167c3f; --ok-bg: #e7f6ec; --warn: #8a5a00; --warn-bg: #fdf3e0;
      --bad: #b02a37; --bad-bg: #fbe9eb; --idle: #6b7280; --idle-bg: #eef0f3;
    }
    @media (prefers-color-scheme: dark) {
      :root {
        --bg: #0f1216; --panel: #171b21; --ink: #e6e8eb; --muted: #98a2b3;
        --line: #262b33; --accent: #4b82f6; --accent-ink: #0f1216;
        --ok: #57c98a; --ok-bg: #12301f; --warn: #e0a94a; --warn-bg: #33260f;
        --bad: #ef8592; --bad-bg: #331519; --idle: #98a2b3; --idle-bg: #1e232b;
      }
    }
    * { box-sizing: border-box; }
    body {
      margin: 0; background: var(--bg); color: var(--ink);
      font: 14px/1.5 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
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
    main { padding: 20px; max-width: 1200px; margin: 0 auto; }
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
    .state-rejected, .state-canceled, .down { background: var(--bad-bg); color: var(--bad); }
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
    /* Login */
    .login-wrap { max-width: 420px; margin: 8vh auto; }
    .login-wrap input[type=text], .login-wrap input[type=password] {
      width: 100%; padding: 10px; border: 1px solid var(--line);
      border-radius: 8px; background: var(--bg); color: var(--ink); font: inherit;
    }
    .login-wrap button {
      margin-top: 10px; width: 100%; padding: 10px; border: none; border-radius: 8px;
      background: var(--accent); color: var(--accent-ink); font: inherit; font-weight: 600; cursor: pointer;
    }
    .login-wrap .err { color: var(--bad); margin: 8px 0; }
    .login-wrap .seam { color: var(--muted); font-size: 12px; margin-top: 14px; }
    """;
}

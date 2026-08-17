namespace Docket.Meta.Web;

/// <summary>
/// Meta's one self-contained stylesheet (no build chain), served as a const from
/// <c>/meta.css</c>. Mirrors the plane dashboard's restrained look — tables, state
/// pills, a scannable grid, forms — and its light/dark <c>prefers-color-scheme</c>
/// block, so the two surfaces feel like one system without sharing a file.
/// </summary>
internal static class MetaCss
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
    nav .logout button, button.link {
      background: none; border: 1px solid var(--line); color: var(--muted);
      border-radius: 6px; padding: 5px 10px; cursor: pointer; font: inherit;
    }
    nav .logout button:hover { color: var(--ink); }
    main { padding: 20px; max-width: 1100px; margin: 0 auto; }
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
    code, .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 12px; }
    a { color: var(--accent); }
    .badge {
      display: inline-block; padding: 2px 8px; border-radius: 999px;
      font-size: 12px; font-weight: 600; background: var(--idle-bg); color: var(--idle);
    }
    .state-ready { background: var(--ok-bg); color: var(--ok); }
    .state-provisioning, .state-suspending, .state-resuming, .state-upgrading,
    .state-destroying, .busy { background: var(--warn-bg); color: var(--warn); }
    .state-suspended { background: var(--idle-bg); color: var(--idle); }
    .state-failed { background: var(--bad-bg); color: var(--bad); }
    .state-destroyed { background: var(--idle-bg); color: var(--muted); text-decoration: line-through; }
    .ok { color: var(--ok); font-weight: 600; }
    .down { color: var(--bad); font-weight: 600; }
    .empty { color: var(--muted); font-style: italic; margin: 8px 0; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 4px; }
    .actions form { display: inline; }
    .btn {
      display: inline-block; padding: 7px 12px; border: 1px solid var(--line);
      border-radius: 8px; background: var(--panel); color: var(--ink);
      font: inherit; cursor: pointer; text-decoration: none;
    }
    .btn.primary { background: var(--accent); color: var(--accent-ink); border-color: transparent; font-weight: 600; }
    .btn.danger { color: var(--bad); border-color: var(--bad); }
    .btn:hover { border-color: var(--accent); }
    label { display: block; margin: 10px 0 4px; font-weight: 600; font-size: 13px; }
    input[type=text], input[type=password], select {
      width: 100%; max-width: 460px; padding: 9px; border: 1px solid var(--line);
      border-radius: 8px; background: var(--bg); color: var(--ink); font: inherit;
    }
    .hint { color: var(--muted); font-size: 12px; margin: 4px 0 0; }
    .err { color: var(--bad); margin: 8px 0; }
    .secret {
      display: block; margin: 8px 0; padding: 12px; border: 1px dashed var(--warn);
      border-radius: 8px; background: var(--warn-bg); color: var(--ink);
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace; word-break: break-all;
    }
    .kv { display: grid; grid-template-columns: 160px 1fr; gap: 6px 16px; margin: 8px 0; }
    .kv dt { color: var(--muted); }
    .kv dd { margin: 0; }
    .login-wrap { max-width: 420px; margin: 8vh auto; }
    .login-wrap button { margin-top: 12px; width: 100%; padding: 10px; border: none;
      border-radius: 8px; background: var(--accent); color: var(--accent-ink); font: inherit; font-weight: 600; cursor: pointer; }
    .step-done { color: var(--ok); }
    .step-failed { color: var(--bad); }
    .step-pending { color: var(--muted); }
    """;
}

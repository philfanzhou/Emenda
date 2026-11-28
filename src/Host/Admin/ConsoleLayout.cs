namespace Emenda.Host.Admin;

internal static class ConsoleLayout
{
    public static string Header(bool intake) => $$"""
<a class="skip" href="#main-content">跳到主要内容</a>
<header class="topbar">
  <span class="brand"><i class="mark" aria-hidden="true"></i>Emenda</span>
  <span class="product">管理控制台</span>
  <nav aria-label="管理导航">
    <a href="/admin" aria-current="{{(intake ? "false" : "page")}}">调用方凭据</a>
    <a href="/admin/extractions" aria-current="{{(intake ? "page" : "false")}}">错题入库</a>
  </nav>
  <span id="who"></span>
  <button id="logout" hidden type="button">退出登录</button>
</header>
""";

    public const string Styles = """
:root { color-scheme: light; --ink:#20253a; --muted:#5f697b; --line:#dce1e9; --accent:#5342af; --tint:#f1eefb; --canvas:#f5f6f9; }
* { box-sizing:border-box; }
body { margin:0; background:var(--canvas); color:var(--ink); font:15px/1.55 Inter,"PingFang SC","Microsoft YaHei",system-ui,sans-serif; }
button,input,textarea,select { font:inherit; }
button,.button,select,input,textarea { border:1px solid #c9cfdb; border-radius:7px; padding:7px 12px; color:var(--ink); background:white; }
button,.button,summary,select { cursor:pointer; }
button,.button { font-size:14px; font-weight:550; }
button:hover,.button:hover { background:#f5f4fb; border-color:#aba4c9; }
button.primary,.primary.button { background:var(--accent); border-color:var(--accent); color:white; }
button.primary:hover { background:#433391; }
button.danger { color:#9a3633; }
button:disabled { cursor:default; opacity:.52; }
:focus-visible { outline:3px solid #7662c7; outline-offset:2px; }
[hidden],.hidden { display:none!important; }
a { color:var(--accent); }
.skip { position:absolute; top:-80px; left:16px; padding:8px; background:white; z-index:5; }
.skip:focus { top:8px; }
.topbar { display:flex; align-items:center; gap:16px; height:68px; padding:0 max(32px,calc((100% - 1360px)/2)); border-bottom:1px solid var(--line); background:white; }
.brand { display:flex; align-items:center; gap:12px; font-size:24px; font-weight:750; letter-spacing:-1px; }
.mark { width:33px; height:33px; border-radius:9px; background:linear-gradient(white,white) 8px 9px / 17px 3px,linear-gradient(white,white) 8px 15px / 12px 3px,linear-gradient(white,white) 8px 21px / 17px 3px; background-color:var(--accent); background-repeat:no-repeat; }
.product { border-left:1px solid var(--line); padding-left:16px; font-weight:600; }
.topbar nav { display:flex; gap:28px; height:100%; margin-left:24px; }
.topbar a { display:grid; align-items:center; text-decoration:none; font-size:14px; color:var(--muted); border-bottom:3px solid transparent; padding-top:3px; }
.topbar a[aria-current=page] { color:var(--accent); border-color:var(--accent); font-weight:650; }
#who { margin-left:auto; font-weight:600; overflow-wrap:anywhere; }
#logout { border:0; color:var(--muted); }
main { max-width:1440px; margin:auto; padding:32px clamp(20px,3vw,40px); }
h1,h2,h3,h4,p { margin:0; }
h1 { font-size:30px; letter-spacing:-.04em; } h2 { font-size:19px; } h3 { font-size:16px; }
.page-title { display:flex; justify-content:space-between; align-items:center; margin-bottom:22px; gap:14px; }
.page-title p { margin-top:4px; }
.card,.panel { border:1px solid var(--line); border-radius:12px; background:white; box-shadow:0 2px 5px #1d294405; min-width:0; }
.row,.spread { display:flex; align-items:center; gap:9px; flex-wrap:wrap; }
.spread { justify-content:space-between; }
.muted,figcaption { color:var(--muted); font-size:13px; }
.mono,code { font:13px/1.5 Menlo,Consolas,monospace; overflow-wrap:anywhere; }
.notice { padding:10px 14px; border:1px solid #d7d2ec; border-radius:7px; background:#f3f1fb; margin-bottom:14px; overflow-wrap:anywhere; }
.notice.error { color:#912c32; border-color:#e6c4c6; background:#fff1f0; }
.notice.warning { color:#785618; background:#fff6df; border-color:#e3d1a6; }
.pill { display:inline-block; border-radius:5px; padding:2px 9px; font-size:12px; font-weight:650; background:#eef0f4; color:#485165; white-space:nowrap; }
.correct,.Succeeded { color:#236b53; background:#e6f3ed; } .incorrect,.Failed { color:#983e39; background:#fae9e7; }
.unknown,.Queued { color:#795813; background:#fff2d8; } .Running,.Staging { color:#365993; background:#e8eefb; }
.login-hero { max-width:540px; margin:65px auto; padding:36px; text-align:center; }
.login-hero p { margin:12px 0 20px; }
summary { font-weight:600; }
""";
}

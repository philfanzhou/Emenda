namespace Emenda.Host.Admin;

/// <summary>
/// The admin console single page: a dependency-free server-rendered document
/// with inline styles and vanilla script. It restores the hosted-login
/// session, offers the SignaCore sign-in navigation, and manages the caller
/// credentials through <c>/admin/api/callers</c>. No secret is persisted in
/// the browser: created keys are shown once and everything else stays
/// server-side.
/// </summary>
internal static class AdminConsolePage
{
    public const string MediaType = "text/html; charset=utf-8";

    public static string Render(AdminConsoleRenderState state) => $$"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Emenda 管理控制台</title>
<style>
{{AdminConsoleLayout.Styles}}
.toolbar,.table-foot,.config { padding:14px 22px; }
.toolbar,.table-foot { border-bottom:1px solid var(--line); }
.filters { display:flex; border-radius:7px; background:#f0f1f5; padding:3px; }
.filters button { border:0; color:var(--muted); background:transparent; }
.filters button[aria-pressed=true] { color:var(--accent); background:white; box-shadow:0 1px 3px #20203920; }
table { width:100%; border-collapse:collapse; text-align:left; }
th { background:#f8f9fb; color:var(--muted); font-size:12px; }
th,td { padding:12px 17px; border-bottom:1px solid #e9ecf2; }
td { font-size:14px; overflow-wrap:anywhere; } td:first-child { max-width:260px; } td:last-child { white-space:nowrap; }
td code { background:#f3f4f8; border-radius:4px; padding:3px 7px; }
.link { color:var(--accent); border-color:transparent; }
.table-foot { border:0; } .config { margin-top:15px; } .config p { margin-top:10px; }
dialog { width:min(480px,90vw); border:1px solid var(--line); border-radius:12px; color:var(--ink); padding:23px; }
dialog::backdrop { background:#21264055; }
dialog input { display:block; width:100%; margin:5px 0 14px; }
dialog label { font-size:14px; font-weight:600; }
dialog h2 { margin-bottom:14px; overflow-wrap:anywhere; }
#newKey { display:block; padding:12px; border-radius:6px; background:var(--tint); margin:10px 0; user-select:all; }
</style>
</head>
<body>
{{AdminConsoleLayout.Header(intake: false)}}
<main id="main-content">
  <div id="authError" class="notice error hidden" role="status"></div>
  <section id="loginCard" class="card login-hero hidden">
    <h2>管理员登录</h2>
    <p>请使用 SignaCore 管理员账户继续。</p>
    <button id="signIn" class="primary" type="button">使用 SignaCore 登录</button>
  </section>
  <section id="console" class="hidden">
    <div class="page-title"><div><h1>调用方凭据</h1><p class="muted">管理各接入服务使用的 <code>X-Emenda-Api-Key</code>。</p></div>
      <button id="openCreate" class="primary" type="button">＋ 新建调用方</button></div>
    <div class="card"><div class="toolbar spread">
      <div class="filters" aria-label="按状态筛选"><button data-filter="all">全部</button><button data-filter="on">已启用</button><button data-filter="off">已停用</button></div>
      <input type="search" id="search" aria-label="搜索调用方名称或备注" placeholder="搜索调用方名称或备注">
    </div><table><thead><tr><th>调用方 / 备注</th><th>密钥</th><th>来源</th><th>状态</th><th>更新时间（本地）</th><th>操作</th></tr></thead>
      <tbody id="callerRows"><tr><td colspan="6">正在读取调用方…</td></tr></tbody></table>
      <div id="count" class="table-foot muted" role="status"></div></div>
    <details class="card config"><summary>凭据来源说明</summary><p>动态凭据覆盖同名部署配置。静态条目由部署配置管理。</p>
      <p class="muted">部署配置 <code>Emenda:Security:Callers</code> · 动态文件 <code>{{System.Net.WebUtility.HtmlEncode(state.DynamicPath)}}</code></p></details>
    <dialog id="createDialog" aria-labelledby="createTitle"><form id="createForm">
      <h2 id="createTitle">新建调用方</h2><label>调用方名称<input id="newName" maxlength="128" placeholder="例如 study-platform" required></label>
      <label>备注（可选）<input id="newNote"></label><div class="row"><button class="primary" id="create">创建凭据</button><button type="button" data-close>取消</button></div>
    </form></dialog>
    <dialog id="actionDialog" aria-labelledby="actionTitle"><h2 id="actionTitle"></h2><div id="actions" class="row"></div></dialog>
    <dialog id="keyDialog" aria-labelledby="keyTitle"><h2 id="keyTitle"></h2><p class="muted">请复制并妥善保存密钥。</p><code id="newKey"></code><button type="button" data-close>关闭</button></dialog>
  </section>
</main>
<script>
(function () {
  'use strict';
  var authErrors = {
    signInFailed: '登录失败，请重试。',
    notAdmin: '当前账户没有管理员权限。',
    identityUnavailable: '无法连接身份服务（SignaCore），请稍后重试。'
  };
  var authErrorBanner = document.getElementById('authError');
  var params = new URLSearchParams(window.location.search);
  if (params.get('authError')) {
    authErrorBanner.textContent = authErrors[params.get('authError')] || '登录失败。';
    authErrorBanner.classList.remove('hidden');
  }
  if (params.get('authResult') === 'signedOut') {
    authErrorBanner.textContent = '已退出登录。';
    authErrorBanner.className = 'notice info';
  }
  if (params.get('authResult') === 'localOnly') {
    authErrorBanner.textContent = '已退出本应用，统一登录退出未完成；再次登录可能自动恢复身份。';
    authErrorBanner.className = 'notice info';
  }

  function show(which) {
    if (which === 'login') {
      document.querySelectorAll('dialog[open]').forEach(dialog => dialog.close());
      document.getElementById('logout').hidden = true;
      document.getElementById('who').textContent = '';
    }
    document.getElementById('loginCard').classList.toggle('hidden', which !== 'login');
    document.getElementById('console').classList.toggle('hidden', which !== 'console');
  }

  function fetchJson(url, options) {
    options = options || {};
    options.headers = options.headers || {};
    options.credentials = 'same-origin';
    return fetch(url, options).then(function (response) {
      if (response.status === 401) { show('login'); throw new Error('unauthenticated'); }
      if (!response.ok) return response.json().then(function (body) {
        throw new Error((body && body.message) || ('HTTP ' + response.status));
      });
      return response.status === 204 ? null : response.json();
    });
  }

  var csrfPromise = null;
  function csrfToken() {
    if (csrfPromise) return csrfPromise;
    csrfPromise = fetch('/admin/auth/oidc/csrf', { credentials: 'same-origin' })
      .then(function (r) { if (!r.ok) throw new Error('csrf'); return r.json(); })
      .then(function (body) { return body.token; })
      .finally(function () { setTimeout(function () { csrfPromise = null; }, 0); });
    return csrfPromise;
  }
  function withCsrf(method, url, body) {
    return csrfToken().then(function (token) {
      return fetchJson(url, {
        method: method,
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
        body: body === undefined ? undefined : JSON.stringify(body)
      });
    });
  }

  const $ = id => document.getElementById(id);
  const labels = { all:'全部', on:'已启用', off:'已停用' };
  let lastCallers = [], filter = 'all', loading = true;
  function element(tag, text, style) {
    const el = document.createElement(tag);
    el.textContent = text;
    if (style) el.className = style;
    return el;
  }
  function action(label, handler, style) {
    const btn = element('button', label, style);
    btn.type = 'button';
    btn.onclick = async () => {
      btn.disabled = true;
      try { await handler(); }
      catch (error) { if (error.message !== 'unauthenticated') alert(error.message); }
      finally { btn.disabled = false; }
    };
    return btn;
  }
  function renderCallers() {
    const term = $('search').value.trim().toLowerCase();
    const visible = lastCallers.filter(c => (filter === 'all' || c.enabled === (filter === 'on'))
      && (c.name + ' ' + (c.note || '')).toLowerCase().includes(term));
    const counts = { all:lastCallers.length, on:lastCallers.filter(c => c.enabled).length };
    counts.off = counts.all - counts.on;
    document.querySelectorAll('[data-filter]').forEach(b => {
      const key = b.dataset.filter;
      b.textContent = labels[key] + ' ' + counts[key];
      b.setAttribute('aria-pressed', String(key === filter));
    });
    const rows = $('callerRows');
    rows.replaceChildren();
    visible.forEach(c => {
      const row = rows.insertRow(), name = row.insertCell();
      name.append(element('strong', c.name), element('div', c.note || '—', 'muted'));
      const dynamic = c.source === 'dynamic';
      row.insertCell().append(element(dynamic ? 'code' : 'span', dynamic ? c.maskedKey : '由部署配置保管'));
      row.insertCell().textContent = dynamic ? '动态文件' : '部署配置';
      row.insertCell().append(element('span', c.enabled ? '已启用' : '已停用', c.enabled ? 'pill correct' : 'pill'));
      row.insertCell().textContent = c.updatedAtUtc ? new Date(c.updatedAtUtc).toLocaleString('zh-CN', {hour12:false}) : '—';
      const actions = row.insertCell();
      if (dynamic) {
        actions.append(action('查看密钥', async () => {
          const body = await fetchJson(callerUrl(c) + '/secret');
          showKey(c.name, body.data.apiKey);
        }, 'link'), action('更多', () => openActions(c)));
      } else actions.textContent = '配置管理';
    });
    if (!visible.length) {
      const cell = rows.insertRow().insertCell(); cell.colSpan = 6;
      cell.textContent = loading ? '读取中…' : lastCallers.length ? '没有匹配的调用方。' : '暂无调用方。';
    }
    $('count').textContent = '显示 ' + visible.length + ' / ' + lastCallers.length + ' 个调用方';
  }
  function callerUrl(c) { return '/admin/api/callers/' + encodeURIComponent(c.name); }
  function openActions(c) {
    const dialog = $('actionDialog');
    $('actionTitle').textContent = c.name;
    $('actions').replaceChildren(
      action('轮换密钥', async () => {
        if (!confirm('轮换 ' + c.name + ' 的密钥？旧密钥会立即失效。')) return;
        dialog.close();
        const result = await withCsrf('POST', callerUrl(c) + '/rotate');
        showKey('已轮换 ' + c.name, result.data.apiKey); await reload();
      }),
      action(c.enabled ? '停用' : '启用', async () => {
        if (c.enabled && !confirm('停用 ' + c.name + '？使用其密钥的请求将被拒绝。')) return;
        dialog.close(); await withCsrf('PUT', callerUrl(c), {enabled:!c.enabled}); await reload();
      }),
      action('删除', async () => {
        if (!confirm('删除 ' + c.name + '？使用其密钥的请求将被拒绝。')) return;
        dialog.close(); await withCsrf('DELETE', callerUrl(c)); await reload();
      }, 'danger'), action('取消', () => dialog.close()));
    dialog.showModal();
  }
  function showKey(title, key) {
    $('keyTitle').textContent = title; $('newKey').textContent = key; $('keyDialog').showModal();
  }
  $('keyDialog').onclose = () => { $('newKey').textContent = ''; };
  document.querySelectorAll('[data-close]').forEach(b => b.onclick = () => b.closest('dialog').close());
  document.querySelectorAll('[data-filter]').forEach(b => b.onclick = () => { filter = b.dataset.filter; renderCallers(); });
  $('search').oninput = renderCallers;
  let requestNumber = 0;
  async function reload() {
    const generation = ++requestNumber;
    loading = true;
    try {
      const body = await fetchJson('/admin/api/callers');
      if (generation !== requestNumber) return;
      lastCallers = body.data; loading = false; renderCallers();
    } catch (error) {
      if (generation !== requestNumber || error.message === 'unauthenticated') return;
      $('callerRows').replaceChildren();
      const cell = $('callerRows').insertRow().insertCell(); cell.colSpan = 6;
      cell.append('读取失败：' + error.message + ' ', action('重试', reload)); $('count').textContent = '';
      lastCallers = []; loading = false;
    }
  }
  $('openCreate').onclick = () => $('createDialog').showModal();
  $('createForm').onsubmit = async event => {
    event.preventDefault();
    const name = $('newName').value.trim(), note = $('newNote').value.trim();
    if (!name) { $('newName').focus(); return; }
    const existing = lastCallers.find(c => c.name.toLowerCase() === name.toLowerCase());
    if (existing && !confirm('已存在调用方 ' + existing.name + '。继续将生成新密钥并覆盖同名凭据，旧密钥立即失效。')) return;
    $('create').disabled = true;
    try {
      const result = await withCsrf('POST', '/admin/api/callers', {name, note});
      $('createDialog').close(); $('createForm').reset(); showKey('已创建 ' + name, result.data.apiKey); await reload();
    } catch (error) { if (error.message !== 'unauthenticated') alert('创建失败：' + error.message); }
    finally { $('create').disabled = false; }
  };
  document.getElementById('signIn').onclick = function () {
    window.location.href = '/admin/auth/oidc/start?returnUrl=%2Fadmin';
  };
  document.getElementById('logout').onclick = function () {
    withCsrf('POST', '/admin/auth/oidc/logout').then(function (body) {
      if (body && body.logoutUrl) { window.location.href = body.logoutUrl; return; }
      window.location.href = '/admin?authResult=localOnly';
    }).catch(function () {
      authErrorBanner.textContent = '退出未完成，请重试。';
      authErrorBanner.className = 'notice error';
    });
  };

  fetch('/admin/auth/oidc/session', { credentials: 'same-origin' }).then(function (r) {
    if (r.status === 503) { $('signIn').disabled = true; document.querySelector('.login-hero p').textContent = '管理端未配置（Emenda:Admin:Oidc 缺少必要配置）。'; show('login'); return null; }
    if (!r.ok) { show('login'); return null; }
    return r.json();
  }).then(function (body) {
    if (!body || !body.data || !body.data.authenticated) { show('login'); return; }
    document.getElementById('who').textContent = body.data.username || '管理员';
    document.getElementById('logout').hidden = false;
    show('console');
    reload();
  }).catch(function () { show('login'); });
})();
</script>
</body>
</html>
""";
}

/// <summary>Values the page template interpolates.</summary>
public sealed record AdminConsoleRenderState(string DynamicPath)
{
    public static AdminConsoleRenderState For(string? dynamicPath) =>
        new(string.IsNullOrWhiteSpace(dynamicPath) ? "未启用（Emenda:Security:DynamicCallers:Path 为空）" : dynamicPath);
}

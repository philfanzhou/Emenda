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
  :root { color-scheme: light; }
  * { box-sizing: border-box; }
  body { font-family: "Segoe UI", "Microsoft YaHei", system-ui, sans-serif; margin: 0; background: #f4f6f9; color: #1c2733; }
  header { background: #16324f; color: #fff; padding: 14px 24px; display: flex; align-items: center; gap: 12px; }
  header h1 { font-size: 18px; margin: 0; flex: 1; }
  header .who { font-size: 13px; opacity: .9; }
  main { max-width: 860px; margin: 24px auto; padding: 0 16px; }
  .card { background: #fff; border: 1px solid #dde4ec; border-radius: 8px; padding: 20px; margin-bottom: 16px; box-shadow: 0 1px 2px rgba(16,32,48,.05); }
  .card h2 { font-size: 15px; margin: 0 0 12px; color: #16324f; }
  .notice { border-radius: 6px; padding: 10px 12px; margin-bottom: 14px; font-size: 13px; }
  .notice.error { background: #fdecea; color: #8a1f18; border: 1px solid #f3c1bc; }
  .notice.info { background: #e8f1fb; color: #1c4d8c; border: 1px solid #bcd6f0; }
  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid #edf1f5; vertical-align: middle; }
  th { color: #5a6b7d; font-weight: 600; background: #fafbfc; }
  td .mono { font-family: Consolas, monospace; font-size: 12px; }
  .pill { display: inline-block; border-radius: 999px; padding: 1px 10px; font-size: 12px; }
  .pill.on { background: #e3f4e8; color: #1d6b33; }
  .pill.off { background: #fdecea; color: #8a1f18; }
  .pill.src { background: #eef0f3; color: #4a5a6a; margin-left: 6px; }
  button { border: 1px solid #b9c6d4; background: #fff; color: #1c2733; border-radius: 6px; padding: 5px 12px; font-size: 13px; cursor: pointer; }
  button:hover { background: #f2f6fa; }
  button.primary { background: #1654a0; border-color: #1654a0; color: #fff; }
  button.primary:hover { background: #12478a; }
  button.danger { color: #8a1f18; border-color: #e3b6b1; }
  button:disabled { opacity: .5; cursor: default; }
  input[type=text] { border: 1px solid #b9c6d4; border-radius: 6px; padding: 6px 10px; font-size: 13px; width: 240px; }
  .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .keybox { background: #0f1e2e; color: #7ee2a8; font-family: Consolas, monospace; font-size: 13px; border-radius: 6px; padding: 10px 12px; word-break: break-all; margin-top: 10px; }
  .muted { color: #6b7a89; font-size: 12px; }
  .hidden { display: none; }
  .login-hero { text-align: center; padding: 44px 16px; }
  .login-hero h2 { font-size: 20px; margin-bottom: 6px; }
  .login-hero p { color: #5a6b7d; font-size: 14px; margin-bottom: 22px; }
  .login-hero button { font-size: 15px; padding: 9px 22px; }
</style>
</head>
<body>
<header>
  <h1>Emenda 管理控制台</h1>
  <span class="who" id="who"></span>
  <button id="logout" class="hidden" type="button">退出登录</button>
</header>
<main>
  <div id="authError" class="notice error hidden"></div>

  <section id="loginCard" class="card login-hero hidden">
    <h2>管理员登录</h2>
    <p>请使用 SignaCore 管理员账户继续。</p>
    <button id="signIn" class="primary" type="button">使用 SignaCore 登录</button>
  </section>

  <section id="console" class="hidden">
    <div class="card">
      <h2>调用方凭据（X-Emenda-Api-Key）</h2>
      <div class="notice info">
        静态配置（<code>Emenda:Security:Callers</code>）与动态凭据文件
        （<code>{{state.DynamicPath}}</code>）按名称合并，动态条目覆盖同名静态条目。
        新建或轮换后的密钥只显示一次。
      </div>
      <div class="row" style="margin-bottom:12px">
        <input type="text" id="newName" placeholder="调用方名称，如 study-platform" maxlength="128">
        <input type="text" id="newNote" placeholder="备注（可选）" style="width:220px">
        <button id="create" class="primary" type="button">新建调用方</button>
      </div>
      <div id="createResult" class="hidden">
        <div class="keybox" id="newKey"></div>
        <div class="muted" style="margin:6px 0 12px">请立即复制保存，此密钥不会再次显示。</div>
      </div>
      <table>
        <thead><tr><th>名称</th><th>密钥</th><th>来源</th><th>状态</th><th>备注 / 更新时间</th><th style="width:230px">操作</th></tr></thead>
        <tbody id="callerRows"><tr><td colspan="6" class="muted">加载中…</td></tr></tbody>
      </table>
    </div>
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

  function show(which) {
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

  function mask(key) {
    if (!key) return '';
    if (key.length <= 8) return '****';
    return key.slice(0, 4) + '****' + key.slice(-4);
  }
  function esc(value) {
    var div = document.createElement('div');
    div.textContent = value == null ? '' : String(value);
    return div.innerHTML;
  }

  var revealed = {};
  function renderCallers(callers) {
    var tbody = document.getElementById('callerRows');
    tbody.innerHTML = '';
    if (!callers.length) {
      tbody.innerHTML = '<tr><td colspan="6" class="muted">尚无调用方。</td></tr>';
      return;
    }
    callers.forEach(function (caller) {
      var tr = document.createElement('tr');
      var keyText = caller.source === 'configuration' ? '<span class="muted">配置文件管理，请查看部署配置</span>'
        : '<span class="mono" data-key="' + esc(caller.name) + '">' + esc(revealed[caller.name] || mask(caller.maskedKey)) + '</span>';
      tr.innerHTML =
        '<td>' + esc(caller.name) + '</td>' +
        '<td>' + keyText + '</td>' +
        '<td>' + (caller.source === 'configuration' ? '配置' : '动态文件') + '</td>' +
        '<td><span class="pill ' + (caller.enabled ? 'on' : 'off') + '">' + (caller.enabled ? '启用' : '停用') + '</span></td>' +
        '<td class="muted">' + esc(caller.note || '') + (caller.updatedAtUtc ? '<br>' + esc(caller.updatedAtUtc) : '') + '</td>' +
        '<td class="row"></td>';
      var actions = tr.lastElementChild;
      if (caller.source === 'dynamic') {
        var reveal = document.createElement('button');
        reveal.type = 'button'; reveal.textContent = '显示';
        reveal.onclick = function () {
          fetchJson('/admin/api/callers/' + encodeURIComponent(caller.name) + '/secret')
            .then(function (body) {
              revealed[caller.name] = body.data.apiKey;
              renderCallers(lastCallers);
            }).catch(function () {});
        };
        actions.appendChild(reveal);
        var rotate = document.createElement('button');
        rotate.type = 'button'; rotate.textContent = '轮换';
        rotate.onclick = function () {
          if (!window.confirm('轮换将立即生成新密钥并替换 ' + caller.name + ' 的旧密钥，调用方需同步更新。继续？')) return;
          withCsrf('POST', '/admin/api/callers/' + encodeURIComponent(caller.name) + '/rotate')
            .then(function (body) { showNewKey('已轮换 ' + caller.name, body.data.apiKey); reload(); })
            .catch(function (error) { alert('轮换失败：' + error.message); });
        };
        actions.appendChild(rotate);
        var toggle = document.createElement('button');
        toggle.type = 'button'; toggle.textContent = caller.enabled ? '停用' : '启用';
        toggle.onclick = function () {
          withCsrf('PUT', '/admin/api/callers/' + encodeURIComponent(caller.name), { enabled: !caller.enabled })
            .then(reload).catch(function (error) { alert('操作失败：' + error.message); });
        };
        actions.appendChild(toggle);
        var remove = document.createElement('button');
        remove.type = 'button'; remove.textContent = '删除'; remove.className = 'danger';
        remove.onclick = function () {
          if (!window.confirm('删除调用方 ' + caller.name + '？使用该密钥的调用将立即失效。')) return;
          withCsrf('DELETE', '/admin/api/callers/' + encodeURIComponent(caller.name))
            .then(reload).catch(function (error) { alert('删除失败：' + error.message); });
        };
        actions.appendChild(remove);
      } else {
        var note = document.createElement('span');
        note.className = 'muted';
        note.textContent = '静态条目：请修改部署配置或用同名动态条目覆盖';
        actions.appendChild(note);
      }
      tbody.appendChild(tr);
    });
  }

  var lastCallers = [];
  function reload() {
    fetchJson('/admin/api/callers').then(function (body) {
      lastCallers = body.data;
      renderCallers(body.data);
    }).catch(function () {});
  }

  function showNewKey(title, key) {
    var box = document.getElementById('createResult');
    document.getElementById('newKey').textContent = title + '：' + key;
    box.classList.remove('hidden');
  }

  document.getElementById('create').onclick = function () {
    var name = document.getElementById('newName').value.trim();
    var note = document.getElementById('newNote').value.trim();
    if (!name) { alert('请输入调用方名称（不含空格）。'); return; }
    withCsrf('POST', '/admin/api/callers', { name: name, note: note })
      .then(function (body) {
        document.getElementById('newName').value = '';
        document.getElementById('newNote').value = '';
        showNewKey('已创建 ' + name, body.data.apiKey);
        reload();
      }).catch(function (error) { alert('创建失败：' + error.message); });
  };

  document.getElementById('signIn').onclick = function () {
    window.location.href = '/admin/auth/oidc/start?returnUrl=%2Fadmin';
  };
  document.getElementById('logout').onclick = function () {
    withCsrf('POST', '/admin/auth/oidc/logout').then(function (body) {
      if (body && body.logoutUrl) { window.location.href = body.logoutUrl; return; }
      window.location.href = '/admin?authResult=signedOut';
    }).catch(function () { window.location.href = '/admin'; });
  };

  fetch('/admin/auth/oidc/session', { credentials: 'same-origin' }).then(function (r) {
    if (r.status === 503) { document.querySelector('.login-hero p').textContent = '管理端未配置（Emenda:Admin:Oidc 缺少必要配置）。'; show('login'); return null; }
    if (!r.ok) { show('login'); return null; }
    return r.json();
  }).then(function (body) {
    if (!body || !body.data || !body.data.authenticated) return;
    document.getElementById('who').textContent = body.data.username || '';
    document.getElementById('logout').classList.remove('hidden');
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

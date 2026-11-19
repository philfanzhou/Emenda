namespace Emenda.Host.Admin;

/// <summary>Cookie-authenticated extraction workspace. All writes are explicit and carry SignaCore CSRF.</summary>
internal static class ExtractionConsolePage
{
    public const string MediaType = "text/html; charset=utf-8";

    public static string Render() => $$"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="same-origin">
<title>图片提取与纠偏 · Emenda</title>
<style>
:root { color-scheme:light; --navy:#16324f; --blue:#1654a0; --ink:#1c2733; --muted:#526477; --line:#c9d4df; --canvas:#f4f6f9; --radius:6px; }
* { box-sizing:border-box; }
body { margin:0; color:var(--ink); background:var(--canvas); font:14px/1.6 "Segoe UI","Microsoft YaHei",system-ui,sans-serif; -webkit-font-smoothing:antialiased; }
a { color:var(--blue); overflow-wrap:anywhere; }
button,input,select,textarea { font:inherit; }
button,.button { min-height:42px; padding:7px 12px; border:1px solid #aebdcd; border-radius:var(--radius); background:#fff; color:var(--ink); cursor:pointer; touch-action:manipulation; }
button:active { transform:scale(.98); }
button.primary { color:#fff; background:var(--blue); border-color:var(--blue); }
button.danger { color:#8a1f18; border-color:#bf706b; }
button:disabled { opacity:.55; cursor:not-allowed; }
:focus-visible { outline:3px solid #cc7211; outline-offset:3px; }
input,select,textarea { min-height:42px; width:100%; padding:8px 10px; border:1px solid #aebdcd; border-radius:var(--radius); background:#fff; color:var(--ink); }
textarea { min-height:100px; resize:vertical; }
input[type=file] { padding:8px; }
label { display:block; font-weight:600; }
label>input,label>select,label>textarea { margin-top:5px; font-weight:400; }
fieldset { min-width:0; border:1px solid var(--line); border-radius:var(--radius); padding:14px; margin:14px 0; }
legend { color:var(--navy); font-weight:700; padding:0 5px; }
header { background:var(--navy); color:#fff; padding:14px 24px; display:flex; align-items:center; gap:16px; flex-wrap:wrap; }
header a { color:#fff; }
header h1 { font-size:18px; margin:0; flex:1; }
header .who { font-size:13px; }
main { max-width:1600px; padding:24px; margin:auto; }
h2 { font-size:20px; line-height:1.4; margin:0 0 14px; color:var(--navy); }
h3 { font-size:16px; margin:0 0 10px; }
h4 { margin:12px 0 6px; font-size:14px; }
p { margin:8px 0; }
[hidden] { display:none!important; }
.skip { position:absolute; top:-100px; left:12px; z-index:20; background:#fff; padding:10px; }
.skip:focus { top:10px; }
.panel { background:#fff; border:1px solid var(--line); border-radius:8px; padding:20px; margin-bottom:18px; }
.workspace { display:grid; grid-template-columns:300px minmax(0,1fr); gap:20px; align-items:start; }
.workspace>* { min-width:0; }
.row { display:flex; align-items:center; gap:10px; flex-wrap:wrap; }
.row>* { max-width:100%; }
.spread { justify-content:space-between; }
.grid { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:14px; }
.upload-grid { display:grid; grid-template-columns:1fr 110px 1.5fr; gap:12px; }
.muted { color:var(--muted); font-size:13px; }
.mono { font:12px/1.6 Consolas,monospace; overflow-wrap:anywhere; font-variant-numeric:tabular-nums; }
.notice { border:1px solid #bcd6f0; border-radius:var(--radius); background:#e8f1fb; color:#1c4d8c; padding:12px; margin:12px 0; overflow-wrap:anywhere; }
.notice.error { color:#8a1f18; background:#fdecea; border-color:#e3b6b1; }
.notice.warning { color:#6b4510; background:#fff6df; border-color:#d8b46c; }
.pill { display:inline-block; padding:3px 9px; border-radius:4px; font-weight:600; background:#eef0f3; color:#36485a; }
.pill.correct { color:#1d6b33; background:#e3f4e8; }
.pill.incorrect { color:#8a1f18; background:#fdecea; }
.pill.unknown { color:#6b4510; background:#fff1cc; }
.job-list { list-style:none; margin:14px 0; padding:0; }
.job-list li { border-bottom:1px solid #e2e8ef; padding:8px 0; }
.job-list button { width:100%; text-align:left; border-color:transparent; display:grid; gap:4px; }
.job-list button[aria-current=true] { border-color:var(--blue); background:#e8f1fb; }
.job-list .mono { font-size:11px; }
.summary { display:flex; gap:8px; flex-wrap:wrap; margin:12px 0; }
.question { border-top:1px solid var(--line); padding:18px 0; }
.question:first-child { border-top:0; }
.read-text { white-space:pre-wrap; overflow-wrap:anywhere; background:#f4f6f9; padding:10px 12px; border-radius:4px; margin:0; }
.source-list { display:grid; grid-template-columns:repeat(auto-fit,minmax(160px,1fr)); gap:12px; }
.source-list figure,.crops figure { margin:0; min-width:0; }
.source-list img { width:100%; height:130px; object-fit:contain; background:#edf1f5; }
figcaption { font-size:12px; color:var(--muted); overflow-wrap:anywhere; }
.crops { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:12px; margin-top:12px; }
.crops img { display:block; max-width:100%; height:auto; max-height:420px; object-fit:contain; border:1px solid var(--line); }
details { margin:12px 0; }
summary { cursor:pointer; min-height:42px; padding:8px 0; font-weight:600; color:var(--navy); }
.editor-layout { display:grid; grid-template-columns:minmax(0,1fr) minmax(260px,.85fr); gap:18px; align-items:start; }
.region-list { list-style:none; padding:0; margin:8px 0; }
.region-row { padding:10px 0; border-bottom:1px solid #e2e8ef; }
.region-row.active { background:#e8f1fb; padding:10px; }
.split-select { display:flex; align-items:center; gap:8px; }
.split-select input { width:24px; min-height:24px; margin:0; }
.coordinates { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:8px; margin:8px 0; }
.coordinates input { padding:6px; font-variant-numeric:tabular-nums; }
.stage { position:relative; line-height:0; background:#edf1f5; width:100%; overflow:hidden; user-select:none; }
.stage img { width:100%; height:auto; display:block; image-orientation:from-image; }
.stage.drawing { cursor:crosshair; touch-action:none; }
.region-box { position:absolute; border:2px solid #8a1f18; background:rgba(138,31,24,.08); pointer-events:none; }
.region-box.selected { border-color:#1654a0; background:rgba(22,84,160,.12); }
.region-box span { position:absolute; top:0; left:0; font:12px/1.6 system-ui,sans-serif; background:#16324f; color:#fff; padding:0 4px; }
.edit-footer { border-top:1px solid var(--line); margin-top:16px; padding-top:16px; }
.history-list { padding-left:20px; }
.history-list li { margin:10px 0; overflow-wrap:anywhere; }
.empty { padding:24px 0; color:var(--muted); }
@media(hover:hover) { button:not(:disabled):hover { background:#edf3fa; } button.primary:not(:disabled):hover { background:#12478a; } }
@media(max-width:1100px) { .editor-layout { grid-template-columns:1fr; } }
@media(max-width:760px) { main { padding:14px; } header { padding:14px; gap:10px; } .workspace,.grid,.upload-grid { grid-template-columns:1fr; } .panel { padding:14px; } .job-list { max-height:340px; overflow:auto; } header h1 { flex-basis:100%; } }
@media(max-width:420px) { .coordinates { grid-template-columns:repeat(2,minmax(0,1fr)); } .row button { flex:1 1 auto; } .crops { grid-template-columns:1fr; } }
@media(prefers-reduced-motion:reduce) { button:active { transform:none; } }
</style>
</head>
<body>
<a class="skip" href="#main-content">跳到图片提取工作区</a>
<header>
  <a href="/admin">Emenda 管理控制台</a>
  <h1>图片提取与纠偏</h1>
  <span id="who" class="who"></span>
  <button id="logout" type="button" hidden>退出登录</button>
</header>
<main id="main-content">
  <div id="message" class="notice" role="status" aria-live="polite" hidden></div>
  <section id="login" class="panel" hidden>
    <h2>管理员登录</h2>
    <p id="loginHelp">使用现有 SignaCore 管理员会话，原图与纠偏操作仅对管理员开放。</p>
    <a class="button" href="/admin/auth/oidc/start?returnUrl=%2Fadmin%2Fextractions" target="_blank" rel="noopener">在新标签使用 SignaCore 登录</a>
    <button id="checkSession" type="button">重新检查会话</button>
  </section>
  <div id="console" hidden>
    <section class="panel" aria-labelledby="uploadTitle">
      <h2 id="uploadTitle">提交学习照片</h2>
      <p class="muted">一图可含多题，多张图可归为同题。保留原图，先组题、再核对实际作答；只有有证据的错误题进入待审核。</p>
      <form id="uploadForm">
        <fieldset id="uploadFields">
          <legend>创建异步提取任务</legend>
          <div class="upload-grid">
            <label>学生 ID<input name="studentId" id="studentId" required maxlength="36" autocomplete="off"></label>
            <label>年级<input name="grade" id="grade" type="number" min="1" max="12" step="1" required value="1"></label>
            <label>来源幂等键 sourceKey<input name="sourceKey" id="sourceKey" required maxlength="64" pattern="[A-Za-z0-9._-]{1,64}" aria-describedby="sourceKeyHelp"></label>
          </div>
          <p id="sourceKeyHelp" class="muted">同一批照片始终使用同一来源键。超时后先查询任务，不自动重复提交；换一批内容必须使用新键。</p>
          <label>原始照片，1 至 {{{Services.Extraction.ExtractionRules.MaxSources}}} 张 JPEG / PNG，每张不超过 15 MiB<input id="images" name="images" type="file" accept="image/jpeg,image/png" multiple required aria-describedby="fileSummary"></label>
          <p id="fileSummary" class="muted">尚未选择照片。</p>
        </fieldset>
        <div class="row"><button id="upload" class="primary" type="submit">上传并创建任务</button><button id="newSourceKey" type="button">为新一批生成来源键</button></div>
        <div id="uploadMessage" class="notice" role="status" hidden></div>
      </form>
    </section>
    <div class="workspace">
      <aside class="panel" aria-labelledby="listTitle">
        <h2 id="listTitle">提取任务</h2>
        <form id="filterForm"><label>按学生 ID 筛选<input id="filterStudent" maxlength="36" autocomplete="off"></label><div class="row" style="margin-top:10px"><button type="submit">筛选 / 刷新</button></div></form>
        <div id="listMessage" role="status" class="muted"></div>
        <ol id="jobs" class="job-list"></ol>
        <div class="row spread"><button id="previous" type="button" disabled>上一页</button><span id="pageLabel" class="muted">第 1 页</span><button id="next" type="button" disabled>下一页</button></div>
      </aside>
      <section id="detail" class="panel" aria-labelledby="detailTitle"><h2 id="detailTitle">任务详情</h2><p class="empty">选择左侧任务，查看原图、判断证据和历史版本。</p></section>
    </div>
  </div>
</main>
<script>
(function () {
  'use strict';
  // No write is retried by request(), polling, session restoration or list refresh.
  const ROOT = '/admin/api/extractions';
  const MAX_FILES = {{{Services.Extraction.ExtractionRules.MaxSources}}}, MAX_FILE_BYTES = 15 * 1024 * 1024, MAX_POLLS = 60;
  const statusNames = { Staging:'原图校验中', Queued:'排队中', Running:'识别与判错中', Succeeded:'处理完成', Failed:'处理失败' };
  const outcomeNames = { correct:'正确', incorrect:'错误', unknown:'无法判断' };
  const state = { authenticated:false, page:1, filter:'', list:[], job:null, selectedId:null, loading:0, pollEpoch:0, pollTimer:null, pollCount:0, polling:false, drafts:new Map(), history:null, historyEpoch:0, busy:false, uploadPending:null, retryKeys:new Map() };
  const $ = id => document.getElementById(id);
  const copy = value => JSON.parse(JSON.stringify(value));
  function uuid() {
    // getRandomValues remains available on the documented LAN HTTP profile.
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
    return hex.slice(0, 8) + '-' + hex.slice(8, 12) + '-' + hex.slice(12, 16) + '-' + hex.slice(16, 20) + '-' + hex.slice(20);
  }
  function node(tag, text, className) {
    const element = document.createElement(tag);
    if (text !== undefined && text !== null) element.textContent = String(text);
    if (className) element.className = className;
    return element;
  }
  function button(text, action, className) {
    const element = node('button', text, className);
    element.type = 'button'; element.addEventListener('click', action); return element;
  }
  function notice(id, text, kind) {
    const target = $(id); target.textContent = text; target.className = 'notice' + (kind ? ' ' + kind : ''); target.hidden = !text;
    target.setAttribute('role', kind === 'error' ? 'alert' : 'status');
  }
  function date(value) { return new Date(value).toLocaleString('zh-CN', { hour12:false }); }
  function jobUrl(id) { return ROOT + '/' + encodeURIComponent(id); }
  function sourceUrl(jobId, sourceId) { return jobUrl(jobId) + '/sources/' + encodeURIComponent(sourceId); }
  function cropUrl(jobId, questionId, version, index) { return jobUrl(jobId) + '/questions/' + encodeURIComponent(questionId) + '/images/' + version + '/' + index; }
  function isActive(job) { return ['Staging','Queued','Running'].includes(job.status); }
  function showLogin(text) {
    state.authenticated = false; stopPolling(); $('login').hidden = false; $('console').hidden = true; $('logout').hidden = true;
    if (text) $('loginHelp').textContent = text;
  }
  async function request(url, options) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), options && options.body instanceof FormData ? 180000 : 30000);
    try {
      const response = await fetch(url, { ...options, credentials:'same-origin', cache:'no-store', redirect:'error', signal:controller.signal });
      let body = null;
      if ((response.headers.get('content-type') || '').includes('application/json')) body = await response.json();
      if (response.status === 401) showLogin('会话已过期。当前页面内的纠偏草稿仍然保留；登录后可重新检查会话。');
      if (!response.ok || (body && body.success === false)) {
        const error = new Error((body && (body.errorCode || body.message)) || 'HTTP ' + response.status);
        error.status = response.status; throw error;
      }
      if (!body) throw new Error('服务未返回 JSON，未将响应视为成功。');
      return body;
    } catch (error) {
      if (error.name === 'AbortError') throw new Error('请求超时，结果尚未确认。请先查询状态，任何写入都不会自动重放。');
      throw error;
    } finally { clearTimeout(timer); }
  }
  async function write(method, url, value) {
    const csrf = await request('/admin/auth/oidc/csrf');
    if (typeof csrf.token !== 'string' || !csrf.token) throw new Error('无法取得 CSRF 令牌，未发送写入。');
    const multipart = value instanceof FormData;
    return request(url, { method, headers:multipart ? { 'X-CSRF-TOKEN':csrf.token } : { 'X-CSRF-TOKEN':csrf.token, 'Content-Type':'application/json' }, body:multipart ? value : value === undefined ? undefined : JSON.stringify(value) });
  }
  async function checkSession() {
    try {
      const response = await request('/admin/auth/oidc/session');
      if (!response.data || !response.data.authenticated) { showLogin(); return; }
      state.authenticated = true; $('who').textContent = response.data.username || '管理员'; $('login').hidden = true; $('console').hidden = false; $('logout').hidden = false;
      await loadList();
      if (state.selectedId) await refreshCurrent(false);
    } catch (error) { showLogin(error.status === 503 ? '管理端未配置 SignaCore，暂时无法使用。' : '无法恢复管理员会话：' + error.message); }
  }
  $('checkSession').onclick = checkSession;
  $('logout').onclick = async function () {
    if (hasUnsavedDraft() && !confirm('退出会丢失本页尚未保存的草稿。继续？')) return;
    try {
      const body = await write('POST', '/admin/auth/oidc/logout');
      state.drafts.clear(); stopPolling();
      const target = body.logoutUrl ? new URL(body.logoutUrl, location.origin) : null;
      if (target && ['https:','http:'].includes(target.protocol)) location.assign(target.href);
      else location.assign('/admin?authResult=localOnly');
    } catch (error) { notice('message', '退出失败：' + error.message, 'error'); }
  };
  function hasUnsavedDraft() { return [...state.drafts.values()].some(draft => draft.dirty || draft.pending); }
  window.addEventListener('beforeunload', event => {
    if (hasUnsavedDraft() || state.uploadPending) { event.preventDefault(); event.returnValue = ''; }
  });
  $('sourceKey').value = uuid();
  $('newSourceKey').onclick = function () {
    if (state.uploadPending && !confirm('上一笔上传结果尚未确认。换键提交可能产生另一个任务，请先查询任务列表。确定为新一批生成键？')) return;
    state.uploadPending = null; $('sourceKey').value = uuid(); $('uploadFields').disabled = false; $('upload').textContent = '上传并创建任务'; notice('uploadMessage', '已生成新来源键。仅在提交不同批次内容时使用。');
  };
  $('images').onchange = function () {
    const files = [...$('images').files];
    $('fileSummary').textContent = files.length ? files.length + ' 张，' + (files.reduce((sum, file) => sum + file.size, 0) / 1048576).toFixed(2) + ' MiB：' + files.map(file => file.name).join('、') : '尚未选择照片。';
  };
  function prepareUpload() {
    const studentId = $('studentId').value.trim(), grade = Number($('grade').value), sourceKey = $('sourceKey').value.trim(), files = [...$('images').files];
    if (!studentId || studentId.length > 36) throw new Error('学生 ID 必须为 1 至 36 个字符。');
    if (!Number.isInteger(grade) || grade < 1 || grade > 12) throw new Error('年级必须为 1 至 12 的整数。');
    if (!/^[A-Za-z0-9._-]{1,64}$/.test(sourceKey)) throw new Error('来源键仅可使用 1 至 64 位字母、数字、点、下划线和连字符。');
    if (!files.length || files.length > MAX_FILES) throw new Error(`请选择 1 至 ${MAX_FILES} 张原图。`);
    for (const file of files) {
      if (!['image/jpeg','image/png'].includes(file.type)) throw new Error(file.name + ' 不是 JPEG / PNG。');
      if (file.size < 1 || file.size > MAX_FILE_BYTES) throw new Error(file.name + ' 为空或超过 15 MiB。');
    }
    const data = new FormData(); data.append('studentId', studentId); data.append('grade', String(grade)); data.append('sourceKey', sourceKey);
    files.forEach(file => data.append('images', file, file.name));
    return { data, studentId, sourceKey };
  }
  $('uploadForm').onsubmit = async function (event) {
    event.preventDefault(); if (state.busy) return;
    try {
      if (!state.uploadPending) state.uploadPending = prepareUpload();
      else if (!confirm('仅重发同一批照片和同一 sourceKey，不更改任何内容。建议先刷新任务列表确认是否已接受。继续？')) return;
      state.busy = true; $('upload').disabled = true; $('newSourceKey').disabled = true; $('uploadFields').disabled = true;
      notice('uploadMessage', '正在提交。重复点击不会创建并行请求。');
      const accepted = await write('POST', ROOT, state.uploadPending.data);
      const submitted = state.uploadPending; state.uploadPending = null;
      $('filterStudent').value = submitted.studentId; state.filter = submitted.studentId; state.page = 1;
      $('images').value = ''; $('fileSummary').textContent = '尚未选择照片。'; $('sourceKey').value = uuid();
      notice('uploadMessage', '任务已接受：' + accepted.data.id + '。sourceKey：' + submitted.sourceKey + '。处理在服务端异步继续。');
      state.busy = false;
      await loadList(); await selectJob(accepted.data.id);
    } catch (error) {
      notice('uploadMessage', '提交未确认：' + error.message + (state.uploadPending ? '。已冻结原始内容与来源键，请先刷新任务列表；需要时可明确重发同一请求。' : ''), 'error');
    } finally {
      state.busy = false; $('upload').disabled = false; $('newSourceKey').disabled = false; $('uploadFields').disabled = !!state.uploadPending; $('upload').textContent = state.uploadPending ? '明确重发同一上传请求' : '上传并创建任务';
    }
  };
  let listEpoch = 0;
  async function loadList() {
    const epoch = ++listEpoch;
    $('listMessage').textContent = '读取中…'; $('previous').disabled = true; $('next').disabled = true;
    try {
      const query = new URLSearchParams({ page:String(state.page) }); if (state.filter) query.set('studentId', state.filter);
      const body = await request(ROOT + '?' + query);
      if (epoch !== listEpoch) return;
      state.list = body.data; renderList();
      $('listMessage').textContent = state.list.length ? '按创建时间倒序，每页最多 20 条。' : '本页没有任务。';
      $('previous').disabled = state.page <= 1; $('next').disabled = state.list.length < 20; $('pageLabel').textContent = '第 ' + state.page + ' 页';
    } catch (error) { if (epoch === listEpoch) { $('listMessage').textContent = '列表读取失败：' + error.message; $('previous').disabled = state.page <= 1; } }
  }
  function renderList() {
    $('jobs').replaceChildren();
    state.list.forEach(job => {
      const li = node('li'), open = button('', () => selectJob(job.id));
      open.setAttribute('aria-current', String(job.id === state.selectedId));
      open.append(node('strong', job.studentId + ' · ' + job.grade + ' 年级'), node('span', statusNames[job.status] || job.status), node('span', job.id, 'mono'), node('span', date(job.createdAt) + ' · v' + job.version, 'muted'));
      li.append(open); $('jobs').append(li);
    });
  }
  $('filterForm').onsubmit = event => { event.preventDefault(); state.filter = $('filterStudent').value.trim(); state.page = 1; loadList(); };
  $('previous').onclick = () => { if (state.page > 1) { state.page--; loadList(); } };
  $('next').onclick = () => { state.page++; loadList(); };
  function stopPolling() { clearTimeout(state.pollTimer); state.pollTimer = null; state.pollEpoch++; state.polling = false; }
  function pollLabel() {
    if (!state.job || !isActive(state.job)) return '后台处理已结束。';
    if (document.hidden) return '页面在后台，轮询已暂停。返回后可手动继续。';
    return state.polling ? '仅查询状态，每 5 秒一次，最多 60 次；已查询 ' + state.pollCount + ' 次。' : '自动查询已停止。可明确检查状态并继续查询，不会重提任务。';
  }
  function startPolling() {
    stopPolling(); state.pollCount = 0;
    if (!state.job || !isActive(state.job) || document.hidden) return;
    state.polling = true; const epoch = state.pollEpoch, id = state.selectedId;
    const tick = async function () {
      if (epoch !== state.pollEpoch || id !== state.selectedId || document.hidden || !state.authenticated) return;
      if (state.pollCount >= MAX_POLLS) { state.polling = false; renderPollLabel(); return; }
      state.pollCount++;
      try {
        const body = await request(jobUrl(id));
        if (epoch !== state.pollEpoch || id !== state.selectedId) return;
        acceptJob(body.data); renderJob();
        if (!isActive(body.data)) { state.polling = false; renderPollLabel(); loadList(); return; }
        state.pollTimer = setTimeout(tick, 5000);
      } catch (error) {
        if (epoch !== state.pollEpoch) return;
        state.polling = false; renderPollLabel(); notice('message', '状态查询已暂停：' + error.message + '。请手动检查，不会重放写入。', 'error');
      }
    };
    state.pollTimer = setTimeout(tick, 5000); renderPollLabel();
  }
  function renderPollLabel() { if ($('pollLabel')) $('pollLabel').textContent = pollLabel(); }
  document.addEventListener('visibilitychange', () => { if (document.hidden) { stopPolling(); renderPollLabel(); } });
  async function selectJob(id) {
    if (state.busy) { notice('message', '写入尚未结束，请等待结果后再切换任务。', 'warning'); return; }
    stopPolling(); state.selectedId = id; state.history = null; state.historyEpoch++; renderList(); const epoch = ++state.loading;
    $('detail').replaceChildren(node('h2', '任务详情'), node('p', '读取任务中…', 'muted'));
    try {
      const body = await request(jobUrl(id));
      if (epoch !== state.loading || id !== state.selectedId) return;
      acceptJob(body.data); renderJob(); startPolling();
    } catch (error) { if (epoch === state.loading) $('detail').append(node('p', '读取失败：' + error.message, 'notice error'), button('重新读取此任务', () => selectJob(id))); }
  }
  function acceptJob(job) {
    state.job = job;
    const retry = state.retryKeys.get(job.id);
    if (retry && (job.attempt > retry.attempt || job.status !== 'Failed')) state.retryKeys.delete(job.id);
    const draft = state.drafts.get(job.id);
    if (draft && (draft.conflict || job.version !== draft.expectedVersion)) { draft.remote = copy(job); draft.conflict = true; }
    state.list = state.list.map(item => item.id === job.id ? job : item); renderList();
  }
  async function refreshCurrent(continuePolling) {
    const id = state.selectedId; if (!id || state.busy) return;
    const epoch = ++state.loading;
    try {
      const body = await request(jobUrl(id)); if (id !== state.selectedId || epoch !== state.loading || state.busy) return;
      acceptJob(body.data); renderJob(); if (continuePolling) startPolling();
    } catch (error) { notice('message', '读取最新任务失败：' + error.message, 'error'); }
  }
  function sourceName(id) {
    const index = state.job.sources.findIndex(source => source.id === id);
    return index < 0 ? '未知来源 ' + id : '原图 ' + (index + 1);
  }
  function printedLabel(question) { return question.questionNumber == null ? '未知' : question.questionNumber; }
  function groupLabel(question, index) { return '列表第 ' + (index + 1) + ' 组 · 印刷题号：' + printedLabel(question) + ' · UUID ' + question.id; }
  function normalizedQuestionNumber(value) { return typeof value === 'string' ? value.trim() || null : null; }
  function renderJob() {
    const job = state.job, root = $('detail'); root.replaceChildren();
    root.append(node('h2', '任务详情'), node('div', job.id, 'mono'));
    const summary = node('div', null, 'summary');
    summary.append(node('span', statusNames[job.status] || job.status, 'pill'), node('span', job.studentId + ' · ' + job.grade + ' 年级', 'pill'), node('span', '当前 v' + job.version + ' · 尝试 ' + job.attempt, 'pill'));
    root.append(summary, node('p', '创建于 ' + date(job.createdAt) + ' · 更新于 ' + date(job.updatedAt), 'muted'));
    const actions = node('div', null, 'row'); actions.append(button(isActive(job) ? '检查状态并继续轮询' : '查询最新状态', () => refreshCurrent(true)));
    if (isActive(job)) actions.append(button('暂停轮询', () => { stopPolling(); renderPollLabel(); }));
    if (job.status === 'Failed') {
      const retry = button(state.retryKeys.has(job.id) ? '明确重发同一重试键' : '明确重试失败任务', retryJob);
      retry.disabled = state.busy || job.attempt >= 5; actions.append(retry);
      root.append(node('p', '失败代码：' + (job.errorCode || '服务未提供错误代码') + '。最多 5 次尝试；不自动重试。', 'notice error'));
    }
    root.append(actions, node('p', pollLabel(), 'muted')); root.lastChild.id = 'pollLabel';
    const sources = node('details'); sources.append(node('summary', '保留原图（' + job.sources.length + '）'));
    const sourceList = node('div', null, 'source-list');
    job.sources.forEach((source, index) => {
      const figure = node('figure'), link = node('a'), image = node('img');
      link.href = sourceUrl(job.id, source.id); link.target = '_blank'; link.rel = 'noopener';
      image.src = link.href; image.alt = '原图 ' + (index + 1) + '，点击打开保留的原始图片'; image.loading = 'lazy'; image.width = source.width; image.height = source.height;
      image.onerror = () => { image.alt = '原图读取失败，请检查会话后重新查询任务'; };
      link.append(image); figure.append(link, node('figcaption', '原图 ' + (index + 1) + ' · 正向显示 ' + source.width + ' × ' + source.height + ' · EXIF ' + (source.orientation || 1)), node('div', source.id, 'mono')); sourceList.append(figure);
    });
    sources.append(sourceList); root.append(sources);
    if (job.version > 0 || job.status === 'Failed') {
      const draft = state.drafts.get(job.id);
      if (draft) renderDraft(root, draft);
      else {
        const bar = node('div', null, 'row spread'); bar.append(node('h3', job.version > 0 ? '当前保存结果 · v' + job.version : '尚无结果版本，可从保留原图手动组题'), button('编辑整组纠偏', () => beginDraft(job.questions, job.version), 'primary')); root.append(bar);
        renderQuestions(root, job.questions, job.version);
      }
      renderHistory(root);
    } else root.append(node('p', isActive(job) ? '原图保留后，题目分组、解答和证据会在任务完成时显示。' : '尚无成功的结果版本。原图可继续追溯。', 'empty'));
    if (state.busy) root.querySelectorAll('button').forEach(control => { control.disabled = true; });
  }
  async function retryJob() {
    const job = state.job; if (state.busy || job.status !== 'Failed' || job.attempt >= 5) return;
    if (!confirm('明确重试本任务？将使用稳定请求键，不自动重放；已有历史仍保留。')) return;
    const retry = state.retryKeys.get(job.id) || { requestKey:uuid(), attempt:job.attempt };
    const key = retry.requestKey; state.retryKeys.set(job.id, retry); state.busy = true; renderJob();
    try {
      const body = await write('POST', jobUrl(job.id) + '/retry', { requestKey:key });
      state.retryKeys.delete(job.id); acceptJob(body.data); notice('message', '重试已接受，请求键：' + key + '。');
    } catch (error) { notice('message', '重试未确认：' + error.message + '。请求键保留为 ' + key + '，请先查询当前状态；仅再次明确点击才重发。', 'error'); }
    finally { state.busy = false; renderJob(); startPolling(); }
  }
  function renderQuestions(root, views, version) {
    const counts = { correct:0, incorrect:0, unknown:0 }; views.forEach(view => { counts[view.question.outcome]++; });
    const totals = node('div', null, 'summary'); Object.keys(outcomeNames).forEach(outcome => totals.append(node('span', outcomeNames[outcome] + ' ' + counts[outcome], 'pill ' + outcome))); root.append(totals);
    if (!views.length) root.append(node('p', '此版本没有有效题目组。', 'empty'));
    views.forEach((view, index) => {
      const question = view.question, article = node('article', null, 'question');
      const heading = node('div', null, 'row'); heading.append(node('h3', '列表第 ' + (index + 1) + ' 组 · 印刷题号：' + printedLabel(question) + ' · 学科 ' + question.subject), node('span', outcomeNames[question.outcome] || question.outcome, 'pill ' + (Object.hasOwn(outcomeNames, question.outcome) ? question.outcome : 'unknown'))); article.append(heading, node('div', '稳定题目 UUID：' + question.id, 'mono'));
      article.append(node('p', view.itemDeleted ? '关联错题已删除，保留关联墓碑；不会自动重建。' : view.itemId ? '关联错题：' + view.itemId : '未创建错题记录。', 'muted'));
      [['题干',question.statement],['学生实际作答',question.studentAnswer || '未记录实际作答'],['参考答案',question.referenceAnswer],['解题过程',question.explanation],['判断证据',question.evidence]].forEach(([label, text]) => article.append(node('h4', label), node('p', text || '未记录', 'read-text')));
      const regions = node('ol'); question.regions.forEach((region, partIndex) => regions.append(node('li', '第 ' + (partIndex + 1) + ' 部分 · ' + sourceName(region.sourceId) + ' · 来源 UUID ' + region.sourceId + ' · [' + [region.x0,region.y0,region.x1,region.y1].join(', ') + ']', 'mono'))); article.append(node('h4', '有序原图区域（第 1 部分起，按阅读顺序生成题图；归一化正向坐标）'), regions);
      if (view.images.length) {
        const crops = node('div', null, 'crops'); view.images.forEach((crop, cropIndex) => {
          const figure = node('figure'), link = node('a'), image = node('img');
          link.href = cropUrl(state.job.id, question.id, version, cropIndex); link.target = '_blank'; link.rel = 'noopener'; image.src = link.href; image.width = crop.width; image.height = crop.height; image.loading = 'lazy'; image.alt = '列表第 ' + (index + 1) + ' 组，印刷题号 ' + printedLabel(question) + '，v' + version + ' 第 ' + (cropIndex + 1) + ' 部分'; image.onerror = () => { image.alt = '此历史题图读取失败，请检查会话后重试'; };
          link.append(image); figure.append(link, node('figcaption', 'v' + version + ' · 第 ' + (cropIndex + 1) + ' 部分 · ' + sourceName(crop.sourceId) + ' · 来源 UUID ' + crop.sourceId), node('div', crop.path, 'mono')); crops.append(figure);
        }); article.append(crops);
      }
      root.append(article);
    });
  }
  function draftOf() { return state.job && state.drafts.get(state.job.id); }
  function beginDraft(views, version, fromHistory) {
    const existing = draftOf();
    if (existing && (existing.dirty || existing.pending) && !confirm('加载会替换现有本地草稿。确定放弃该草稿？')) return;
    const questions = copy(views.map(view => ({ ...view.question, questionNumber:view.question.questionNumber ?? null })));
    const draft = { expectedVersion:version, baseQuestions:copy(state.job.questions.map(view => view.question)), questions, reason:fromHistory ? '从历史 v' + fromHistory + ' 加载并人工复核' : '', dirty:!!fromHistory, pending:null, conflict:false, remote:null, selectedQuestion:questions[0] ? questions[0].id : null, selectedRegion:0, splitRegions:new Set(), selectedSource:state.job.sources[0] ? state.job.sources[0].id : null, drawing:false };
    state.drafts.set(state.job.id, draft); renderJob(); if ($('draftHeading')) $('draftHeading').focus();
  }
  function changed(draft) {
    draft.dirty = true; draft.pending = null;
    if ($('draftState')) $('draftState').textContent = '有未保存修改。请对照原图核对当前作答、区域及判断证据。';
  }
  function selectedQuestion(draft) { return draft.questions.find(question => question.id === draft.selectedQuestion); }
  function renderDraft(root, draft) {
    const form = node('form'); form.id = 'revisionForm'; form.onsubmit = event => { event.preventDefault(); saveDraft(); };
    const title = node('h3', '整组纠偏草稿 · 基于 v' + draft.expectedVersion); title.id = 'draftHeading'; title.tabIndex = -1;
    form.append(title, node('p', '一次保存完整题目组集合。列表位置不是印刷题号；印刷题号可重复、可未知，不用于自动合并。既有 UUID 不变；新增 / 拆分组使用新 UUID；移除组不物理删除来源或历史。', 'muted'));
    const draftState = node('p', draft.pending ? '上次保存尚未确认，载荷和请求键已冻结。先查询最新版本，或明确重发同一请求。' : draft.dirty ? '有未保存修改。' : '尚未修改。'); draftState.id = 'draftState'; draftState.className = 'notice'; form.append(draftState);
    if (draft.pending) {
      form.append(node('div', '保存请求键：' + draft.pending.requestKey, 'mono'), button('查询最新版本后，显式选择重新基线', async () => {
        draft.conflict = true; await refreshCurrent(false);
      }));
    }
    if (draft.conflict) renderConflict(form, draft);
    const fields = node('fieldset'); fields.disabled = state.busy || !!draft.pending; fields.append(node('legend', '完整题目分组，最多 32 组'));
    const groupRow = node('div', null, 'row'); const chooser = node('select'); chooser.setAttribute('aria-label', '选择要编辑的题目组');
    draft.questions.forEach((question, index) => { const option = node('option', groupLabel(question, index)); option.value = question.id; chooser.append(option); });
    chooser.value = draft.selectedQuestion || ''; chooser.onchange = () => { draft.selectedQuestion = chooser.value; draft.selectedRegion = 0; draft.splitRegions.clear(); draft.drawing = false; renderJob(); };
    groupRow.append(chooser, button('新增题目组', () => {
      if (draft.questions.length >= 32) return;
      const question = { id:uuid(), questionNumber:null, subject:1, statement:'', studentAnswer:null, referenceAnswer:'', explanation:'', outcome:'unknown', evidence:'', regions:[] };
      draft.questions = [...draft.questions, question]; draft.selectedQuestion = question.id; draft.selectedRegion = 0; draft.splitRegions.clear(); changed(draft); renderJob();
    })); fields.append(groupRow);
    const question = selectedQuestion(draft);
    if (question) {
      fields.append(node('p', '稳定题目 UUID：' + question.id, 'mono'));
      const groupActions = node('div', null, 'row');
      const index = draft.questions.indexOf(question);
      const moveGroup = offset => { const next = [...draft.questions]; [next[index], next[index + offset]] = [next[index + offset], next[index]]; draft.questions = next; changed(draft); renderJob(); };
      const up = button('题目组上移', () => moveGroup(-1)); up.disabled = index === 0;
      const down = button('题目组下移', () => moveGroup(1)); down.disabled = index === draft.questions.length - 1;
      groupActions.append(up, down, button('移除此题目组', () => {
        if (!confirm('从当前分组中移除此组？保存后会撤回它的当前关联，历史不删除。')) return;
        draft.questions = draft.questions.filter(item => item.id !== question.id); draft.selectedQuestion = draft.questions[0] ? draft.questions[0].id : null; draft.selectedRegion = 0; draft.splitRegions.clear(); changed(draft); renderJob();
      }, 'danger')); fields.append(groupActions);
      renderMergeControls(fields, draft, question);
      const columns = node('div', null, 'editor-layout'), text = node('div'), geometry = node('div');
      renderQuestionFields(text, draft, question); renderRegions(geometry, draft, question); columns.append(text, geometry); fields.append(columns);
    } else fields.append(node('p', '当前草稿没有题目组。可以新增；保存空集合会撤回全部当前分组。', 'notice warning'));
    const reason = node('label', '纠偏原因（写入版本记录）'), reasonInput = node('textarea'); reasonInput.id = 'revisionReason'; reasonInput.maxLength = 2000; reasonInput.required = true; reasonInput.value = draft.reason; reasonInput.oninput = () => { draft.reason = reasonInput.value; draft.dirty = true; draft.pending = null; }; reason.append(reasonInput); fields.append(reason); form.append(fields);
    const footer = node('div', null, 'edit-footer row');
    const save = node('button', draft.pending ? '明确重发同一保存请求' : '原子保存完整分组', 'primary'); save.type = 'submit'; save.disabled = state.busy || draft.conflict || !['Succeeded','Failed'].includes(state.job.status);
    footer.append(save, button('只读取最新版本，保留草稿', () => refreshCurrent(false)), button('放弃本地草稿', () => {
      if (!confirm('放弃尚未保存的本地草稿？若上次写入结果未知，请先读取最新版本。')) return;
      state.drafts.delete(state.job.id); renderJob();
    })); form.append(footer); root.append(form);
    const saved = node('details'); saved.append(node('summary', '对照当前已保存结果 · v' + state.job.version)); renderQuestions(saved, state.job.questions, state.job.version); root.append(saved);
  }
  function renderMergeControls(root, draft, question) {
    if (draft.questions.length < 2) return;
    const label = node('label', '将当前题目组合并到目标组（按 UUID 选择，不按题号匹配）'), target = node('select');
    draft.questions.forEach((item, index) => { if (item.id !== question.id) { const option = node('option', groupLabel(item, index)); option.value = item.id; target.append(option); } }); label.append(target);
    const merge = button('明确合并到目标组', () => {
      const destination = draft.questions.find(item => item.id === target.value);
      if (!destination || destination.id === question.id || state.busy || draft.pending) return;
      if (destination.regions.length + question.regions.length > 32) { notice('message', '合并后超过 32 个区域，未修改草稿。请先核对区域。', 'error'); return; }
      const sourceIdentity = 'UUID ' + question.id + '，印刷题号：' + printedLabel(question);
      const merged = { ...destination, outcome:'unknown', regions:[...destination.regions, ...question.regions] };
      for (const field of ['statement','studentAnswer','referenceAnswer','explanation']) {
        if (question[field]) merged[field] = (destination[field] ? destination[field] + '\n\n' : '') + '【合并来源：' + sourceIdentity + '】\n' + question[field];
      }
      merged.evidence = (destination.evidence ? destination.evidence + '\n\n' : '') + '【合并来源：' + sourceIdentity + '，学科 ' + question.subject + '，原判定：' + (outcomeNames[question.outcome] || question.outcome) + '】\n' + (question.evidence || '来源组未记录判断证据。');
      if ((merged.statement || '').length > 8000 || (merged.studentAnswer || '').length > 8000 || [merged.referenceAnswer,merged.explanation,merged.evidence].some(value => (value || '').length > 16000)) { notice('message', '合并文字将超过字段长度限制，未修改草稿，也未截断内容。请先人工核对并整理文字。', 'error'); return; }
      if (!confirm('将当前组 ' + sourceIdentity + ' 合并到目标 UUID ' + destination.id + '？目标 UUID、印刷题号和学科保留，判定重置为无法判断。当前组区域按原顺序追加在目标区域后，全部文字按字段追加，来源题号、学科与原判定保留在判断证据。来源组从当前集合移除；保存后历史仍保留。请重新核对合并后的作答与判断。此操作只改草稿。')) return;
      draft.questions = draft.questions.filter(item => item.id !== question.id).map(item => item.id === destination.id ? merged : item);
      draft.selectedQuestion = destination.id; draft.selectedRegion = 0; draft.selectedSource = merged.regions[0] ? merged.regions[0].sourceId : draft.selectedSource; draft.splitRegions.clear(); draft.drawing = false; changed(draft); renderJob();
      notice('message', '已合并到目标 UUID ' + destination.id + '，尚未保存。请核对题号、文字、判定及第 1 部分起的阅读顺序，填写原因后原子保存完整分组。');
    });
    root.append(label, merge, node('p', '只在人工确认属于同一道题时合并；相同题号不会触发合并。目标区域在前、当前组区域在后，可再用区域上移 / 下移调整。', 'muted'));
  }
  function splitSelectedRegions(draft, question) {
    const regions = question.regions.filter(region => draft.splitRegions.has(region));
    if (!regions.length || regions.length === question.regions.length || draft.questions.length >= 32 || state.busy || draft.pending) return;
    if (!confirm('将勾选的 ' + regions.length + ' 个部分按现有阅读顺序拆为新 UUID？原组保留 UUID、题号和未勾选部分，新组题号留空。全部文字、作答与证据会复制供复核，但两组判定均重置为无法判断，不能沿用拆分前的结论。此操作只改草稿，保存后才生成新版本。')) return;
    const created = { ...question, id:uuid(), questionNumber:null, outcome:'unknown', regions:regions.map(region => ({ ...region })) };
    const remaining = { ...question, outcome:'unknown', regions:question.regions.filter(region => !draft.splitRegions.has(region)) };
    draft.questions = draft.questions.flatMap(item => item.id === question.id ? [remaining, created] : [item]);
    draft.selectedQuestion = created.id; draft.selectedRegion = 0; draft.selectedSource = created.regions[0].sourceId; draft.splitRegions.clear(); draft.drawing = false; changed(draft); renderJob();
    notice('message', '已拆为新 UUID ' + created.id + '，尚未保存。原组和新组保留全部文字，请分别核对题号、题干、作答、判定及部分顺序，再填写原因保存完整分组。');
  }
  function renderQuestionFields(root, draft, question) {
    const numberLabel = node('label', '印刷题号（可重复，未知请留空）'), numberInput = node('input');
    numberInput.id = 'questionNumber'; numberInput.type = 'text'; numberInput.maxLength = 64; numberInput.value = question.questionNumber ?? ''; numberInput.autocomplete = 'off'; numberInput.setAttribute('aria-describedby', 'questionNumberHelp');
    numberInput.oninput = () => {
      question.questionNumber = normalizedQuestionNumber(numberInput.value);
      const invalid = question.questionNumber !== null && (question.questionNumber.length > 64 || /[\u0000-\u001f\u007f-\u009f]/.test(question.questionNumber));
      numberInput.setCustomValidity(invalid ? '印刷题号最多 64 个字符，不能含控制字符。' : ''); numberInput.setAttribute('aria-invalid', String(invalid)); changed(draft);
    };
    numberInput.onblur = () => { numberInput.value = question.questionNumber ?? ''; };
    numberLabel.append(numberInput); const numberHelp = node('p', '最多 64 个字符；自动去除首尾空白，空白保存为未知。印刷题号不是 UUID，也不是列表位置。', 'muted'); numberHelp.id = 'questionNumberHelp'; root.append(numberLabel, numberHelp);
    const pair = node('div', null, 'grid'), subjectLabel = node('label', '学科代码（1 至 9）'), subject = node('input');
    subject.type = 'number'; subject.min = '1'; subject.max = '9'; subject.step = '1'; subject.required = true; subject.value = question.subject;
    subject.oninput = () => { question.subject = Number(subject.value); changed(draft); }; subjectLabel.append(subject);
    const outcomeLabel = node('label', '判定结论'), outcome = node('select');
    Object.entries(outcomeNames).forEach(([value, label]) => { const option = node('option', label); option.value = value; outcome.append(option); }); outcome.value = question.outcome;
    outcome.onchange = () => { question.outcome = outcome.value; changed(draft); }; outcomeLabel.append(outcome); pair.append(subjectLabel, outcomeLabel); root.append(pair);
    const specs = [['statement','完整题干、选项和共用材料',8000],['studentAnswer','学生实际作答（没有作答请留空）',8000],['referenceAnswer','参考答案或评分依据',16000],['explanation','解题过程',16000],['evidence','判断证据：引用可核对的实际作答、参考依据及比对结论',16000]];
    specs.forEach(([key, labelText, maximum]) => {
      const label = node('label', labelText), input = node('textarea'); input.maxLength = maximum; input.value = question[key] || ''; input.required = key === 'statement'; input.dataset.field = key;
      input.oninput = () => { question[key] = key === 'studentAnswer' && !input.value.trim() ? null : input.value; changed(draft); }; label.append(input); root.append(label);
    });
    root.append(node('p', '请对照原图核实实际作答、参考答案、区域与判断证据。正确 / 错误结论必须具备真实作答及参考依据；未作答、看不清或材料不足应选“无法判断”。裁剪调整不会自行清空已保存的作答和判断。', 'muted'));
  }
  function renderRegions(root, draft, question) {
    root.append(node('h4', '原图区域与跨页顺序'), node('p', '坐标采用正向显示原图的 0 至 1 比例。可用数字输入完整操作，不必拖动。第 1 部分起的列表顺序就是阅读及题图生成顺序，与印刷题号无关。', 'muted'));
    const sources = node('label', '框选所用原图'), source = node('select');
    state.job.sources.forEach((item, index) => { const option = node('option', '原图 ' + (index + 1) + ' · ' + item.width + ' × ' + item.height); option.value = item.id; source.append(option); });
    const selected = question.regions[draft.selectedRegion]; if (selected) draft.selectedSource = selected.sourceId;
    source.value = draft.selectedSource || ''; source.onchange = () => { draft.selectedSource = source.value; draft.selectedRegion = -1; draft.drawing = false; renderJob(); }; sources.append(source); root.append(sources);
    const controls = node('div', null, 'row');
    controls.append(button('添加此原图完整区域', () => {
      if (question.regions.length >= 32 || !draft.selectedSource) return;
      question.regions = [...question.regions, { sourceId:draft.selectedSource, x0:0, y0:0, x1:1, y1:1 }]; draft.selectedRegion = question.regions.length - 1; changed(draft); renderJob();
    }));
    const draw = button(draft.drawing ? '取消框选' : selected ? '拖动重画选中区域' : '拖动新增区域', () => { draft.drawing = !draft.drawing; renderJob(); }); draw.setAttribute('aria-pressed', String(draft.drawing)); draw.disabled = !draft.selectedSource || (!selected && question.regions.length >= 32); controls.append(draw); root.append(controls);
    const imageSource = state.job.sources.find(item => item.id === draft.selectedSource);
    if (imageSource) {
      const stage = node('div', null, 'stage' + (draft.drawing ? ' drawing' : '')); stage.setAttribute('role', 'group'); stage.setAttribute('aria-label', '当前原图和区域框，使用下方数字输入可完成同样操作');
      const image = node('img'); image.src = sourceUrl(state.job.id, imageSource.id); image.alt = sourceName(imageSource.id) + '，不可变原图，正向显示'; image.width = imageSource.width; image.height = imageSource.height; image.draggable = false; stage.append(image);
      image.onerror = () => { image.alt = '原图读取失败，不能框选。请检查会话并重新查询任务。'; draft.drawing = false; stage.classList.remove('drawing'); };
      question.regions.forEach((region, index) => { if (region.sourceId === imageSource.id) stage.append(regionBox(region, index, index === draft.selectedRegion)); });
      wireRectangle(stage, image, draft, question); root.append(stage, node('p', draft.drawing ? '框选模式：从起点拖到终点。Esc 取消；触屏仅在本模式阻止页面滚动。' : '先选择“拖动新增 / 重画区域”再框选，或使用下方数字输入。', 'muted'));
    }
    const splitHelp = node('p', '勾选要拆出的部分，至少保留 1 个部分在原组中。拆分保留原组 UUID，新组使用新 UUID；内容会完整复制，需人工复核。', 'muted'); splitHelp.id = 'splitHelp';
    const split = button('将勾选部分拆为新题目组', () => splitSelectedRegions(draft, question)); split.setAttribute('aria-describedby', 'splitHelp');
    const splitState = node('p', null, 'muted'); splitState.setAttribute('role', 'status');
    const updateSplit = () => {
      const count = question.regions.filter(region => draft.splitRegions.has(region)).length;
      split.disabled = !count || count === question.regions.length || draft.questions.length >= 32;
      splitState.textContent = '已勾选 ' + count + ' / ' + question.regions.length + ' 个部分。' + (draft.questions.length >= 32 ? '已有 32 组，不能再拆分。' : count === question.regions.length && count ? '必须保留至少 1 个部分在原组。' : '');
    };
    updateSplit(); root.append(splitHelp, split, splitState);
    const list = node('ol', null, 'region-list');
    question.regions.forEach((region, index) => {
      const li = node('li', null, 'region-row' + (index === draft.selectedRegion ? ' active' : ''));
      const splitLabel = node('label', null, 'split-select'), checkbox = node('input'); checkbox.type = 'checkbox'; checkbox.checked = draft.splitRegions.has(region); checkbox.setAttribute('aria-describedby', 'splitHelp');
      checkbox.onchange = () => { if (checkbox.checked) draft.splitRegions.add(region); else draft.splitRegions.delete(region); updateSplit(); }; splitLabel.append(checkbox, node('span', '拆出第 ' + (index + 1) + ' 部分')); li.append(splitLabel);
      li.append(button('选择第 ' + (index + 1) + ' 部分 · ' + sourceName(region.sourceId), () => { draft.selectedRegion = index; draft.selectedSource = region.sourceId; draft.drawing = false; renderJob(); }), node('div', '来源 UUID：' + region.sourceId, 'mono'));
      const label = node('label', '第 ' + (index + 1) + ' 部分来源'), selector = node('select');
      state.job.sources.forEach((item, sourceIndex) => { const option = node('option', '原图 ' + (sourceIndex + 1) + ' · UUID ' + item.id); option.value = item.id; selector.append(option); }); selector.value = region.sourceId;
      selector.onchange = () => { region.sourceId = selector.value; draft.selectedRegion = index; draft.selectedSource = selector.value; changed(draft); renderJob(); }; label.append(selector); li.append(label);
      const coords = node('div', null, 'coordinates');
      ['x0','y0','x1','y1'].forEach(key => {
        const coordinateLabel = node('label', key), input = node('input'); input.type = 'number'; input.min = '0'; input.max = '1'; input.step = 'any'; input.required = true; input.value = region[key]; input.setAttribute('aria-label', '第 ' + (index + 1) + ' 部分的 ' + key);
        input.oninput = () => { region[key] = input.value === '' ? NaN : Number(input.value); changed(draft); };
        input.onchange = () => {
          root.querySelectorAll('.region-box').forEach(box => box.remove());
          const stage = root.querySelector('.stage');
          if (stage) question.regions.forEach((value, position) => { if (value.sourceId === draft.selectedSource) stage.append(regionBox(value, position, position === draft.selectedRegion)); });
        }; coordinateLabel.append(input); coords.append(coordinateLabel);
      }); li.append(coords);
      const moves = node('div', null, 'row'), move = offset => {
        const next = [...question.regions]; [next[index],next[index + offset]] = [next[index + offset],next[index]]; question.regions = next; draft.selectedRegion = index + offset; changed(draft); renderJob();
      };
      const up = button('区域上移', () => move(-1)); up.disabled = index === 0; const down = button('区域下移', () => move(1)); down.disabled = index === question.regions.length - 1;
      moves.append(up, down, button('移除区域', () => { draft.splitRegions.delete(region); question.regions = question.regions.filter((_, position) => position !== index); draft.selectedRegion = Math.min(index, question.regions.length - 1); changed(draft); renderJob(); }, 'danger')); li.append(moves); list.append(li);
    }); root.append(list);
  }
  function regionBox(region, index, selected) {
    const box = node('div', null, 'region-box' + (selected ? ' selected' : ''));
    Object.assign(box.style, { left:region.x0 * 100 + '%', top:region.y0 * 100 + '%', width:(region.x1 - region.x0) * 100 + '%', height:(region.y1 - region.y0) * 100 + '%' });
    box.append(node('span', String(index + 1))); box.setAttribute('aria-hidden', 'true'); return box;
  }
  function wireRectangle(stage, image, draft, question) {
    let drag = null, preview = null;
    const point = event => { const rect = image.getBoundingClientRect(); return { x:Math.min(1,Math.max(0,(event.clientX - rect.left) / rect.width)), y:Math.min(1,Math.max(0,(event.clientY - rect.top) / rect.height)) }; };
    const cancel = () => { if (preview) preview.remove(); preview = null; drag = null; };
    stage.onpointerdown = event => {
      if (!draft.drawing || event.button !== 0 || !image.complete || !image.naturalWidth || state.busy || draft.pending) return;
      event.preventDefault(); drag = { ...point(event), pointer:event.pointerId }; stage.setPointerCapture(event.pointerId);
      preview = regionBox({ x0:drag.x, y0:drag.y, x1:drag.x, y1:drag.y }, draft.selectedRegion >= 0 ? draft.selectedRegion : question.regions.length, true); stage.append(preview);
    };
    stage.onpointermove = event => {
      if (!drag || drag.pointer !== event.pointerId) return;
      const end = point(event); Object.assign(preview.style, { left:Math.min(drag.x,end.x) * 100 + '%', top:Math.min(drag.y,end.y) * 100 + '%', width:Math.abs(drag.x - end.x) * 100 + '%', height:Math.abs(drag.y - end.y) * 100 + '%' });
    };
    stage.onpointerup = event => {
      if (!drag || drag.pointer !== event.pointerId) return;
      const end = point(event), region = { sourceId:draft.selectedSource, x0:Math.min(drag.x,end.x), y0:Math.min(drag.y,end.y), x1:Math.max(drag.x,end.x), y1:Math.max(drag.y,end.y) };
      cancel();
      if (region.x1 <= region.x0 || region.y1 <= region.y0) { notice('message', '区域必须有实际宽度与高度。可以改用数字编辑。', 'warning'); return; }
      if (draft.selectedRegion >= 0 && question.regions[draft.selectedRegion]) question.regions = question.regions.map((value, index) => index === draft.selectedRegion ? region : value);
      else { question.regions = [...question.regions, region]; draft.selectedRegion = question.regions.length - 1; }
      draft.drawing = false; changed(draft); renderJob();
    };
    stage.onpointercancel = cancel; stage.onlostpointercapture = cancel;
  }
  document.addEventListener('keydown', event => {
    const draft = draftOf(); if (event.key === 'Escape' && draft && draft.drawing) { draft.drawing = false; renderJob(); }
  });
  function validateDraft(draft) {
    if (!draft.reason.trim() || draft.reason.length > 2000) return '请填写 1 至 2000 字的纠偏原因。';
    if (draft.questions.length > 32) return '最多保存 32 个题目组。';
    const ids = new Set(), sources = new Set(state.job.sources.map(source => source.id));
    for (let index = 0; index < draft.questions.length; index++) {
      const question = draft.questions[index], prefix = '题目组 ' + (index + 1) + '：';
      if (ids.has(question.id)) return prefix + 'UUID 重复，不能保存。'; ids.add(question.id);
      const questionNumber = normalizedQuestionNumber(question.questionNumber);
      if (questionNumber !== null && (questionNumber.length > 64 || /[\u0000-\u001f\u007f-\u009f]/.test(questionNumber))) return prefix + '印刷题号最多 64 个字符，不能含控制字符。';
      if (!Number.isInteger(question.subject) || question.subject < 1 || question.subject > 9) return prefix + '学科必须为 1 至 9。';
      if (!question.statement.trim() || question.statement.length > 8000) return prefix + '必须有实际题干，且不超过 8000 字。';
      if ((question.studentAnswer || '').length > 8000 || [question.referenceAnswer,question.explanation,question.evidence].some(text => text.length > 16000)) return prefix + '作答最多 8000 字，参考答案、过程、证据各最多 16000 字。';
      if (!Object.hasOwn(outcomeNames, question.outcome)) return prefix + '请选择正确、错误或无法判断。';
      if (!question.regions.length || question.regions.length > 32) return prefix + '必须包含 1 至 32 个原图区域。';
      for (const region of question.regions) {
        if (!sources.has(region.sourceId) || ![region.x0,region.y0,region.x1,region.y1].every(value => Number.isFinite(value) && value >= 0 && value <= 1) || region.x0 >= region.x1 || region.y0 >= region.y1) return prefix + '区域来源无效或坐标无效。必须 0 ≤ x0 < x1 ≤ 1，0 ≤ y0 < y1 ≤ 1。';
      }
      if (question.outcome !== 'unknown') {
        if (!(question.studentAnswer || '').trim() || !question.referenceAnswer.trim() || !question.evidence.trim()) return prefix + '正确 / 错误必须有实际作答、参考答案及比对证据；否则请选择无法判断。';
      }
    }
    return null;
  }
  async function saveDraft() {
    const draft = draftOf(), jobId = state.job.id;
    if (state.busy || !draft || draft.conflict || !['Succeeded','Failed'].includes(state.job.status)) return;
    if (!draft.pending) {
      const validation = validateDraft(draft); if (validation) { notice('message', validation, 'error'); return; }
      if (!draft.questions.length && !confirm('将撤回全部当前题目组。来源、历史及错题既有日志保留。确认保存空集合？')) return;
      draft.pending = { requestKey:uuid(), expectedVersion:draft.expectedVersion, reason:draft.reason.trim(), questions:copy(draft.questions.map(question => ({ ...question, questionNumber:normalizedQuestionNumber(question.questionNumber) }))) };
    } else if (!confirm('仅重发相同 requestKey 和冻结的完整载荷，不创建新的保存请求。继续？')) return;
    state.busy = true; renderJob();
    state.loading++;
    try {
      const body = await write('PUT', jobUrl(jobId) + '/questions', draft.pending);
      const acceptedKey = draft.pending.requestKey; state.drafts.delete(jobId); acceptJob(body.data);
      notice('message', '完整分组已保存为 v' + body.data.version + '。请求键：' + acceptedKey + '。');
      try {
        const latest = await request(jobUrl(jobId)); acceptJob(latest.data);
      } catch (error) {
        notice('message', '保存请求已接受为 v' + body.data.version + '，但最新状态读取失败：' + error.message + '。当前显示该请求的已接受结果，请明确查询最新状态。', 'warning');
      }
    } catch (error) {
      if (error.status === 409) { draft.conflict = true; draft.remote = null; }
      else if (error.status >= 400 && error.status < 500) draft.pending = null;
      notice('message', error.status === 409 ? '保存冲突（409），本地草稿及原请求完整保留。请显式读取最新版本，再选择重新基线或放弃草稿；未自动覆盖或重试。' : draft.pending ? '保存未确认：' + error.message + '。冻结载荷与请求键已保留；先查询当前版本，需要时再明确重发。' : '保存被拒绝：' + error.message + '。本地草稿保留，可修改后明确重新保存。', 'error');
    } finally { state.busy = false; renderJob(); }
  }
  function renderConflict(root, draft) {
    const conflict = node('section', null, 'notice warning'); conflict.append(node('h4', '版本变化 / 保存冲突，本地草稿未覆盖'), node('p', '先读取最新版本，再明确选择如何处理完整分组。不会自动合并、换键重试或静默撤回其他管理员新增的组。'));
    conflict.append(button('读取最新版本并保留本地草稿', () => refreshCurrent(false)));
    const remote = draft.remote;
    if (remote) {
      const baseline = new Map(draft.baseQuestions.map(question => [question.id, JSON.stringify(question)]));
      const latest = new Map(remote.questions.map(view => [view.question.id, JSON.stringify(view.question)]));
      const added = [...latest.keys()].filter(id => !baseline.has(id)), removed = [...baseline.keys()].filter(id => !latest.has(id)), modified = [...latest.keys()].filter(id => baseline.has(id) && baseline.get(id) !== latest.get(id));
      conflict.append(node('p', '最新 v' + remote.version + '，相对草稿基线：新增 ' + added.length + ' 组，撤回 ' + removed.length + ' 组，修改 ' + modified.length + ' 组。'));
      const comparison = node('details'); comparison.append(node('summary', '查看最新版本完整结果（只读）')); renderQuestions(comparison, remote.questions, remote.version); conflict.append(comparison);
      conflict.append(button('保留本地完整分组，以最新版本重新基线', () => {
        if (!confirm('将以本地完整分组替代最新 v' + remote.version + ' 的整组结果。远端新增但本地没有的组会被撤回；远端修改可能被覆盖。确认已逐项对照？这一步只改草稿，不发送写入。')) return;
        draft.expectedVersion = remote.version; draft.baseQuestions = copy(remote.questions.map(view => view.question)); draft.remote = null; draft.conflict = false; draft.pending = null; draft.dirty = true; renderJob(); notice('message', '已显式重新基线，草稿未提交。请重新核验证据，填写原因，再保存新版本。');
      }), button('放弃本地分组，使用最新结果', () => {
        if (!confirm('放弃本地完整草稿并加载最新版本？')) return;
        state.drafts.delete(state.job.id); beginDraft(remote.questions, remote.version);
      }));
    }
    root.append(conflict);
  }
  function renderHistory(root) {
    const history = node('details'); history.open = !!state.history; history.append(node('summary', '历史版本（' + state.job.versions.length + '），只读快照'));
    const list = node('ol', null, 'history-list');
    [...state.job.versions].sort((left, right) => right.version - left.version).forEach(version => {
      const li = node('li'); li.append(button('查看 v' + version.version, () => loadHistory(version.version)), node('div', date(version.createdAt) + ' · ' + version.actor + ' · 模型 ' + version.model, 'muted'), node('p', version.reason || '未记录原因'), node('div', '请求键：' + version.requestKey, 'mono')); list.append(li);
    }); history.append(list);
    if (state.history) {
      const selected = state.history; history.append(node('h3', '历史 v' + selected.version + ' · 只读，不代表当前结果'));
      const controls = node('div', null, 'row'); controls.append(button('关闭历史快照', () => { state.history = null; state.historyEpoch++; renderJob(); }), button('将此历史版本显式加载为当前草稿', () => loadHistoricalDraft(selected)));
      history.append(controls); renderQuestions(history, selected.snapshot.questions, selected.version);
    }
    root.append(history);
  }
  async function loadHistory(version) {
    const id = state.selectedId, epoch = ++state.historyEpoch;
    try {
      const body = await request(jobUrl(id) + '/versions/' + version);
      if (id !== state.selectedId || epoch !== state.historyEpoch) return;
      state.history = { version, snapshot:body.data }; renderJob();
    } catch (error) { notice('message', '历史版本读取失败：' + error.message, 'error'); }
  }
  async function loadHistoricalDraft(history) {
    if (state.busy) return;
    const id = state.selectedId, epoch = ++state.loading;
    try {
      const body = await request(jobUrl(id)); if (id !== state.selectedId || epoch !== state.loading || state.busy) return;
      acceptJob(body.data);
      if (!['Succeeded','Failed'].includes(body.data.status)) throw new Error('当前任务正在处理，不能编辑。');
      beginDraft(history.snapshot.questions, body.data.version, history.version);
    } catch (error) { notice('message', '加载历史到草稿失败：' + error.message, 'error'); }
  }
  const authErrors = { signInFailed:'登录失败，请重新登录。', notAdmin:'当前账户没有管理员权限。', identityUnavailable:'无法连接 SignaCore，请稍后重试。' };
  const authError = new URLSearchParams(location.search).get('authError'); if (authError) notice('message', authErrors[authError] || '登录失败。', 'error');
  checkSession();
})();
</script>
</body>
</html>
""";
}

namespace Emenda.Host.Admin;

/// <summary>Cookie-authenticated extraction workspace. All writes are explicit and carry SignaCore CSRF.</summary>
internal static class ExtractionPage
{
    public static string Render() => $$"""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="same-origin">
<title>错题入库 · Emenda</title>
<style>
{{ConsoleLayout.Styles}}
.workspace { display:grid; grid-template-columns:250px minmax(0,1fr); gap:18px; align-items:start; }
.workspace.editing { grid-template-columns:minmax(0,1fr); }
.workspace.editing aside { display:none; }
aside { position:sticky; top:12px; }
.queue-head,.queue-footer { padding:14px 16px; }
.queue-head { border-bottom:1px solid var(--line); }
#filterStudent { width:100%; margin:10px 0 6px; }
.queue-footer { border-top:1px solid var(--line); }
.queue-footer button { padding:4px 7px; }
.job-list { list-style:none; padding:5px 7px; margin:0; max-height:55vh; overflow:auto; }
.job-list button { width:100%; border:1px solid transparent; text-align:left; padding:10px 9px; }
.job-list button[aria-current=true] { background:var(--tint); border-color:#c6bde5; }
.job-list .delete-task { width:auto; text-align:center; margin:0 9px 8px; }
.cleanup-status { margin:8px 7px; padding:8px; border-top:1px solid var(--line); }
.job-list .spread { gap:4px; } .job-list strong { overflow-wrap:anywhere; }
.task-head,.section-body { padding:20px 22px; }
.task-head p,.read-text { margin-top:5px; }
.tabs,.question-nav { display:flex; gap:12px; border-bottom:1px solid var(--line); padding:0 22px; }
.tabs button { border:0; border-radius:0; border-bottom:2px solid transparent; padding:9px 3px; color:var(--muted); }
.tabs button[aria-pressed=true] { border-color:var(--accent); color:var(--accent); }
.question-nav { gap:7px; padding:12px 22px; background:#fafafd; overflow-x:auto; }
.question-nav button { white-space:nowrap; font-size:13px; }
.question-nav button[aria-pressed=true] { background:var(--tint); border-color:#b2a5da; color:var(--accent); }
.evidence,.editor-layout,.grid,.answers { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:18px; }
.evidence { margin:16px 0; } .evidence h4 { margin:0 0 6px; font-size:13px; color:var(--muted); }
.answers { gap:8px; margin:12px 0; } .answers>div { background:var(--canvas); padding:10px 12px; border-radius:6px; }
.read-text { overflow-wrap:anywhere; white-space:pre-wrap; } .evidence p { margin-bottom:12px; }
figure { margin:0; min-width:0; } .crops figure { border:1px solid var(--line); border-radius:7px; overflow:hidden; margin-bottom:10px; }
.crops img,.source-list img { display:block; width:100%; height:auto; max-height:370px; object-fit:contain; background:#f2f3f6; }
.crops img { padding:18px; } figcaption { padding:8px 12px; overflow-wrap:anywhere; }
.source-list { display:grid; grid-template-columns:repeat(auto-fill,minmax(175px,1fr)); gap:14px; }
.source-list img { height:145px; } .source-list .mono { padding:0 12px; }
.empty { padding:22px; color:var(--muted); }
.section-body details,.section-body .question { margin-top:14px; }
summary { padding:7px 0; }
fieldset { border:0; padding:0; margin:0; min-width:0; }
label { display:block; font-size:13px; font-weight:600; }
label input:not([type=checkbox]), label textarea, label select { display:block; margin:4px 0 9px; width:100%; font-weight:400; }
textarea { min-height:60px; resize:vertical; }
.editor-layout { border-top:1px solid var(--line); margin-top:14px; padding-top:14px; }
.editor-layout>* { min-width:0; }
.group-bar { flex-wrap:nowrap; }
.group-bar h2 { white-space:nowrap; }
.group-bar .question-nav { padding:3px; min-width:0; max-width:72%; border:0; background:transparent; }
.editor-meta { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:10px; }
.text-fields { margin-top:8px; gap:0 10px; }
.text-fields label { grid-column:1 / -1; }
.text-fields .answer-field { grid-column:auto; }
.text-fields textarea { min-height:43px; height:48px; }
#revisionForm h3 { margin-bottom:8px; }
.editor-layout .spread select { max-width:60%; }
#draftState { margin:7px 0; }
.edit-footer { position:sticky; bottom:0; background:white; border-top:1px solid var(--line); padding:12px 0; margin-top:10px; }
.edit-footer label { flex:1; } #revisionReason { min-height:39px; height:39px; margin:3px 0 0; }
.image-scroll { max-height:37vh; overflow:auto; border:1px solid var(--line); background:#eff1f5; padding:10px; border-radius:6px; margin:8px 0; }
.stage { position:relative; line-height:0; width:100%; user-select:none; } .stage img { width:100%; height:auto; display:block; image-orientation:from-image; }
.stage.drawing { cursor:crosshair; touch-action:none; }
.region-box { position:absolute; pointer-events:none; border:2px solid #975f4d; background:#985c4810; }
.region-box.selected { border-color:var(--accent); background:#5342af14; } .region-box span { background:var(--accent); color:white; font:12px/1.6 monospace; padding:0 4px; }
.region-list { padding-left:20px; } .region-row { border-top:1px solid var(--line); padding:8px 0; }
.coordinates { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:7px; } .coordinates input { padding:6px; }
.history-list li { padding:5px 0; overflow-wrap:anywhere; }
</style>
</head>
<body>
{{ConsoleLayout.Header(intake: true)}}
<main id="main-content">
  <div id="message" class="notice" role="status" aria-live="polite" hidden></div>
  <section id="login" class="panel login-hero" hidden>
    <h2>管理员登录</h2>
    <p id="loginHelp">使用现有 SignaCore 管理员会话，原图与纠偏操作仅对管理员开放。</p>
    <a class="button" href="/admin/auth/oidc/start?returnUrl=%2Fadmin%2Fextractions" target="_blank" rel="noopener">在新标签使用 SignaCore 登录</a>
    <button id="checkSession" type="button">重新检查会话</button>
  </section>
  <div id="console" hidden>
    <div class="page-title"><div><h1 id="pageTitle">错题入库</h1>
      <p id="pageHelp" class="muted">核对客户端提交的作业。有实际作答和错误证据的题目会生成待审核错题。</p>
    </div><button id="returnToTask" type="button" hidden>返回任务详情</button></div>
    <div class="workspace" id="workspace">
      <aside id="taskList" class="panel" aria-labelledby="listTitle">
        <form id="filterForm" class="queue-head">
          <div class="spread"><h2 id="listTitle">入库任务</h2><button type="submit">查询 / 刷新</button></div>
          <input id="filterStudent" type="search" placeholder="按学生 ID 筛选" aria-label="按学生 ID 筛选" maxlength="36" autocomplete="off">
          <div id="listMessage" role="status" class="muted"></div>
        </form>
        <ol id="jobs" class="job-list"></ol>
        <div id="cleanupStatus" class="cleanup-status" hidden><p id="cleanupMessage" role="status"></p><div class="row"><button id="checkCleanup" type="button">查询清理状态</button><button id="retryCleanup" type="button" class="danger" hidden>重试清理</button></div></div>
        <div class="queue-footer spread"><button id="previous" type="button" disabled>上一页</button><span id="pageLabel" class="muted">第 1 页</span><button id="next" type="button" disabled>下一页</button></div>
      </aside>
      <section id="detail" class="panel" aria-label="任务详情"><p class="empty">选择入库任务，查看判题结果和原图。</p></section>
    </div>
  </div>
</main>
{{CorrectionPanel.Script}}
<script>
(function () {
  'use strict';
  // No write is retried by request(), polling, session restoration or list refresh.
  const ROOT = '/admin/api/extractions';
  const MAX_POLLS = 60;
  const statusNames = { Staging:'原图校验中', Queued:'排队中', Running:'识别与判错中', Succeeded:'处理完成', Failed:'处理失败' };
  const outcomeNames = { correct:'正确', incorrect:'错误', unknown:'无法判断' };
  const state = { authenticated:false, page:1, filter:'', list:[], job:null, selectedId:null, loading:0, pollEpoch:0, pollTimer:null, pollCount:0, polling:false, drafts:new Map(), busy:false, section:'判题结果', questionId:null, editing:false, retryKeys:new Map() };
  let cleanupJobId = null;
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
  // Lists and cards show display variants; links and the box editor keep the full original.
  function sized(url, size) { return size ? url + '?size=' + size : url; }
  function sourceUrl(jobId, sourceId, size) { return sized(jobUrl(jobId) + '/sources/' + encodeURIComponent(sourceId), size); }
  function cropUrl(jobId, questionId, version, index, size) { return sized(jobUrl(jobId) + '/questions/' + encodeURIComponent(questionId) + '/images/' + version + '/' + index, size); }
  function isActive(job) { return ['Staging','Queued','Running'].includes(job.status); }
  function showLogin(text) {
    $('who').textContent = '';
    state.authenticated = false; stopPolling(); $('login').hidden = false; $('console').hidden = true; $('logout').hidden = true;
    if (text) $('loginHelp').textContent = text;
  }
  async function request(url, options) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 30000);
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
    return request(url, { method, headers:{ 'X-CSRF-TOKEN':csrf.token, ...(multipart ? {} : {'Content-Type':'application/json'}) }, body:multipart ? value : value === undefined ? undefined : JSON.stringify(value) });
  }
  async function checkSession() {
    try {
      const response = await request('/admin/auth/oidc/session');
      if (!response.data || !response.data.authenticated) { showLogin(); return; }
      state.authenticated = true; $('who').textContent = response.data.username || '管理员'; $('login').hidden = true; $('console').hidden = false; $('logout').hidden = false;
      await loadList();
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
    if (hasUnsavedDraft()) { event.preventDefault(); event.returnValue = ''; }
  });
  let listEpoch = 0;
  async function loadList(refreshDetail = true) {
    if (state.busy) return;
    const epoch = ++listEpoch;
    $('detail').inert = true;
    $('listMessage').textContent = '读取中…'; $('previous').disabled = true; $('next').disabled = true;
    try {
      const query = new URLSearchParams({ page:String(state.page) }); if (state.filter) query.set('studentId', state.filter);
      const body = await request(ROOT + '/summary?' + query);
      if (epoch !== listEpoch) return;
      if (!body.data.items.length && state.page > 1) {
        state.page--; await loadList(refreshDetail); return;
      }
      state.list = body.data.items; renderList();
      $('listMessage').textContent = state.list.length ? '' : '未找到入库任务。';
      $('previous').disabled = state.page <= 1; $('next').disabled = !body.data.nextPage; $('pageLabel').textContent = '第 ' + state.page + ' 页';
      if (!state.list.some(job => job.id === state.selectedId)) {
        if (state.list.length) await selectJob(state.list[0].id);
        else {
          stopPolling(); state.loading++; state.selectedId = null; state.job = null;
          state.editing = false; renderJob();
        }
      } else if (!state.job || state.job.id !== state.selectedId) await selectJob(state.selectedId);
      else if (refreshDetail) await refreshCurrent(false);
    } catch (error) { if (epoch === listEpoch) { $('listMessage').textContent = '列表读取失败：' + error.message; $('previous').disabled = state.page <= 1; } }
    finally { if (epoch === listEpoch) $('detail').inert = false; }
  }
  function renderList() {
    $('jobs').replaceChildren();
    state.list.forEach(job => {
      const li = node('li'), open = button('', () => selectJob(job.id));
      open.setAttribute('aria-current', String(job.id === state.selectedId));
      const line = node('div', null, 'spread');
      line.append(node('strong', job.studentId), statusBadge(job)); open.title = job.id;
      open.append(line, node('span', job.caller + ' · ' + date(job.createdAt), 'muted'), node('div', job.id, 'mono'),
        node('div', '错题 ' + job.mistakeCount + ' · 已确认 ' + job.confirmedCount, 'muted'));
      const remove = button('删除任务', () => deleteExtraction(job), 'danger delete-task');
      remove.setAttribute('aria-label', '删除任务 ' + job.id);
      remove.disabled = state.busy;
      li.append(open, remove); $('jobs').append(li);
    });
  }
  function showCleanup(receipt) {
    const states = { Pending:'任务已移除，文件正在清理', Failed:'部分文件清理失败，可重试', Completed:'清理完成' };
    $('cleanupStatus').hidden = false;
    $('cleanupMessage').textContent = receipt.jobId + '：' + (states[receipt.status] || receipt.status) + '。删除错题 ' +
      receipt.deletedMistakes + ' 道，保留已确认题 ' + receipt.retainedConfirmed + ' 道。';
    $('retryCleanup').hidden = receipt.status !== 'Failed';
  }
  async function deleteExtraction(job, retry = false) {
    if (state.busy) return;
    if (!retry && state.drafts.has(job.id)) {
      notice('message', '该任务有未保存的纠偏，请先保存或放弃草稿。', 'warning'); return;
    }
    if (!window.confirm(retry ? '重试清理任务 ' + job.id + ' 的剩余文件？' :
      '删除试卷任务 ' + job.id + '？当前已确认的错题会保留。')) return;
    cleanupJobId = job.id;
    state.busy = true; renderList(); stopPolling();
    $('checkCleanup').disabled = true; $('retryCleanup').disabled = true;
    try {
      showCleanup((await write('DELETE', jobUrl(job.id))).data);
    } catch (error) {
      $('cleanupStatus').hidden = false; $('retryCleanup').hidden = false;
      $('cleanupMessage').textContent = '未确认清理结果：' + error.message + '。请先查询状态。';
    } finally {
      state.busy = false; $('checkCleanup').disabled = false; $('retryCleanup').disabled = false;
      await loadList(false);
    }
  }
  $('checkCleanup').onclick = async () => {
    if (!cleanupJobId || state.busy) return;
    const id = cleanupJobId;
    try { const body = await request(jobUrl(id) + '/deletion'); if (id === cleanupJobId) showCleanup(body.data); }
    catch (error) { if (id === cleanupJobId) $('cleanupMessage').textContent = '无法确认清理状态：' + error.message; }
  };
  $('retryCleanup').onclick = () => { if (cleanupJobId) deleteExtraction({ id:cleanupJobId }, true); };
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
        if (!isActive(body.data)) { state.polling = false; renderPollLabel(); loadList(false); return; }
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
    stopPolling(); state.section = '判题结果'; state.questionId = null; state.editing = false; state.job = null; state.selectedId = id; renderList(); const epoch = ++state.loading;
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
    state.list = state.list.map(item => item.id === job.id ? { ...item, status:job.status } : item); renderList();
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
  function statusBadge(job) {
    return node('span', statusNames[job.status] || job.status, 'pill ' + job.status);
  }
  function renderJob() {
    const job = state.job, root = $('detail'), draft = draftOf();
    const editing = !!draft && state.editing;
    root.replaceChildren();
    $('workspace').classList.toggle('editing', editing);
    $('returnToTask').hidden = !editing;
    $('returnToTask').disabled = state.busy;
    $('taskList').inert = state.busy;
    $('pageTitle').textContent = editing ? '整组纠偏' : '错题入库';
    $('pageHelp').textContent = editing ? job.studentId + ' · ' + job.grade + ' 年级 · 基于已保存版本 v' + draft.expectedVersion
      : '核对客户端提交的作业。有实际作答和错误证据的题目会生成待审核错题。';
    if (!job) {
      root.append(node('p', '本页没有入库任务。', 'empty'));
      return;
    }
    if (editing) {
      const body = node('div', null, 'section-body'); renderDraft(body, draft); root.append(body);
    } else {
      const head = node('div', null, 'task-head'), title = node('div', null, 'row'), bar = node('div', null, 'spread');
      title.append(node('h2', job.studentId), statusBadge(job));
      const actions = node('div', null, 'row');
      actions.append(button(isActive(job) ? '检查状态并继续轮询' : '查询最新状态', () => refreshCurrent(true)));
      if (isActive(job)) actions.append(button('暂停轮询', () => { stopPolling(); renderPollLabel(); }));
      bar.append(title, actions); head.append(bar, node('p', job.grade + ' 年级 · ' + date(job.createdAt) + ' 创建 · 当前版本 v' + job.version, 'muted'));
      if (isActive(job)) { const label = node('p', pollLabel(), 'muted'); label.id = 'pollLabel'; head.append(label); }
      if (job.status === 'Failed') {
        const retry = button(state.retryKeys.has(job.id) ? '明确重发同一重试键' : '重试失败任务', retryJob);
        retry.disabled = job.attempt >= 5;
        head.append(node('p', '失败原因：' + (job.errorCode || '原因未知') + ' · 已尝试 ' + job.attempt + ' / 5 次', 'notice error'), retry);
      }
      root.append(head);
      const tabs = node('div', null, 'tabs');
      for (const [name, suffix] of [['判题结果',''],['保留原图',' · ' + job.sources.length],['版本历史',' · ' + job.versions.length],['任务信息','']]) {
        const control = button(name + suffix, () => { state.section = name; renderJob(); });
        control.setAttribute('aria-pressed', String(name === state.section)); tabs.append(control);
      }
      root.append(tabs);
      const body = node('div', null, 'section-body');
      switch (state.section) {
        case '保留原图': renderSources(body, job); break;
        case '版本历史': renderHistory(body); break;
        case '任务信息':
          body.append(node('h3', '任务信息'), node('p', job.id, 'mono'), node('p', '最近更新：' + date(job.updatedAt) + ' · 第 ' + job.attempt + ' 次尝试', 'muted'));
          break;
        default:
          if (job.version > 0 || job.status === 'Failed') {
            const selected = job.questions.find(view => view.question.id === state.questionId) || job.questions[0];
            if (selected) {
              const picker = node('div', null, 'question-nav');
              job.questions.forEach(({question}, index) => {
                const label = question.questionNumber ? '题号 ' + question.questionNumber : '第 ' + (index + 1) + ' 组';
                const pick = button(label + ' · ' + outcomeNames[question.outcome], () => { state.questionId = question.id; renderJob(); });
                pick.title = groupLabel(question, index);
                pick.setAttribute('aria-pressed', String(question.id === selected.question.id)); picker.append(pick);
              });
              root.append(picker);
            }
            const edit = button(draft ? '继续整组纠偏' : '整组纠偏', () => {
              if (draft) { state.editing = true; renderJob(); }
              else beginDraft(job.questions, job.version);
            }, 'primary');
            if (selected) renderQuestion(body, selected, job.questions.indexOf(selected), job.version, edit);
            else body.append(node('p', '当前版本没有题目组。可对照原图手动组题。', 'empty'), edit);
          } else body.append(node('p', '任务完成后会在这里显示题目、作答和判定证据。', 'empty'));
      }
      root.append(body);
      if (job.status === 'Succeeded') window.renderEmendaCorrections(root, {
        job, selected:job.questions.find(view => view.question.id === state.questionId) || job.questions[0],
        read:request, write, error:message => notice('message', message, 'error'),
        setBusy:value => { state.busy = value; root.inert = value; $('taskList').inert = value; },
        reload:async () => { const body = await request(jobUrl(job.id)); acceptJob(body.data); renderJob(); }
      });
    }
    const selectedChip = root.querySelector('.question-nav button[aria-pressed=true]');
    if (selectedChip) {
      const strip = selectedChip.parentElement;
      strip.scrollLeft += selectedChip.getBoundingClientRect().left - strip.getBoundingClientRect().left - strip.clientWidth / 2 + selectedChip.offsetWidth / 2;
    }
    root.inert = state.busy;
  }
  $('returnToTask').onclick = () => { state.editing = false; state.section = '判题结果'; renderJob(); };
  function renderSources(root, job) {
    const list = node('div', null, 'source-list');
    job.sources.forEach((source, index) => {
      const figure = node('figure'), link = node('a'), image = node('img');
      link.href = sourceUrl(job.id, source.id); link.target = '_blank'; link.rel = 'noopener';
      image.src = sourceUrl(job.id, source.id, 'thumbnail'); image.loading = 'lazy'; image.alt = '查看原图 ' + (index + 1);
      link.append(image); figure.append(link, node('figcaption', '原图 ' + (index + 1) + ' · ' + source.width + ' × ' + source.height + ' · EXIF ' + (source.orientation || 1)), node('div', source.id, 'mono'));
      list.append(figure);
    });
    root.append(job.sources.length ? list : node('p', '暂无保留原图。', 'empty'));
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
    if (!views.length) root.append(node('p', '此版本没有题目组。', 'empty'));
    views.forEach((view, index) => renderQuestion(root, view, index, version));
  }
  function renderQuestion(root, view, index, version, edit) {
    const q = view.question, article = node('article', null, 'question');
    const heading = node('div', null, 'spread'), title = node('div', null, 'row');
    title.append(node('h2', '印刷题号 ' + printedLabel(q)), node('span', outcomeNames[q.outcome], 'pill ' + q.outcome));
    heading.append(title); if (edit) heading.append(edit);
    article.append(heading, node('p', '第 ' + (index + 1) + ' 组 · 学科代码 ' + q.subject, 'muted'));
    const evidence = node('div', null, 'evidence'), crops = node('div', null, 'crops'), text = node('div');
    crops.append(node('h4', '原图证据'));
    if (!view.images.length) crops.append(node('p', '此版本没有题图。', 'muted'));
    const identifiers = node('details');
    identifiers.append(node('summary', '关联错题与区域坐标'), node('div', '题目 UUID：' + q.id, 'mono'),
      node('p', view.itemDeleted ? '关联错题已删除。' : view.itemId ? '关联错题：' + view.itemId : '未创建错题记录。', 'muted'));
    q.regions.forEach((region, i) => identifiers.append(node('div', '第 ' + (i + 1) + ' 部分 · ' + region.sourceId + ' · [' + [region.x0,region.y0,region.x1,region.y1].join(', ') + ']', 'mono')));
    view.images.forEach((crop, i) => {
      const figure = node('figure'), link = node('a'), image = node('img'), caption = node('figcaption', null, 'spread');
      link.href = cropUrl(state.job.id, q.id, version, i); link.target = '_blank'; link.rel = 'noopener';
      image.src = cropUrl(state.job.id, q.id, version, i, 'small'); image.alt = '查看第 ' + (i + 1) + ' 部分的 v' + version + ' 裁图'; image.loading = 'lazy';
      link.append(image);
      const original = node('a', '查看原图 ↗'); original.href = sourceUrl(state.job.id, crop.sourceId); original.target = '_blank'; original.rel = 'noopener';
      caption.append(node('span', sourceName(crop.sourceId) + ' · 第 ' + (i + 1) + ' 部分'), original);
      figure.append(link, caption); crops.append(figure); identifiers.append(node('div', crop.path, 'mono'));
    });
    const answers = node('div', null, 'answers');
    for (const [label, value] of [['学生实际作答', q.studentAnswer], ['参考答案', q.referenceAnswer]]) {
      const box = node('div'); box.append(node('h4', label), node('strong', value || '未记录', 'read-text')); answers.append(box);
    }
    text.append(node('h4', '题干'), node('p', q.statement, 'read-text'), answers,
      node('h4', '判断证据'), node('p', q.evidence || '未记录', 'read-text'), node('h4', '解题过程'), node('p', q.explanation || '未记录', 'read-text'));
    evidence.append(crops, text); article.append(evidence, identifiers); root.append(article);
  }
  function draftOf() { return state.job && state.drafts.get(state.job.id); }
  function beginDraft(views, version) {
    const existing = draftOf();
    if (existing && (existing.dirty || existing.pending) && !confirm('加载会替换现有本地草稿。确定放弃该草稿？')) return;
    const questions = copy(views.map(view => ({ ...view.question, questionNumber:view.question.questionNumber ?? null })));
    const draft = { expectedVersion:version, baseQuestions:copy(state.job.questions.map(view => view.question)), questions, reason:'', dirty:false, pending:null, conflict:false, remote:null, selectedQuestion:questions.find(q => q.id === state.questionId)?.id ?? questions[0]?.id ?? null, selectedRegion:0, splitRegions:new Set(), selectedSource:state.job.sources[0] ? state.job.sources[0].id : null, drawing:false };
    state.section = '判题结果'; state.editing = true; state.drafts.set(state.job.id, draft); renderJob(); if ($('draftHeading')) $('draftHeading').focus();
  }
  function changed(draft) {
    draft.dirty = true; draft.pending = null;
    if ($('draftState')) $('draftState').textContent = '有未保存修改';
  }
  function selectedQuestion(draft) { return draft.questions.find(question => question.id === draft.selectedQuestion); }
  function renderDraft(root, draft) {
    const form = node('form'); form.id = 'revisionForm'; form.onsubmit = event => { event.preventDefault(); saveDraft(); };
    const title = node('h2', '题目组'); title.id = 'draftHeading'; title.tabIndex = -1;
    const draftState = node('p', draft.pending ? '上次保存尚未确认，载荷和请求键已冻结。先查询最新版本，或明确重发同一请求。' : draft.dirty ? '有未保存修改。' : '尚未修改。'); draftState.id = 'draftState'; draftState.className = draft.pending ? 'notice warning' : 'muted';
    if (draft.pending) form.append(draftState);
    if (draft.pending) {
      form.append(node('div', '保存请求键：' + draft.pending.requestKey, 'mono'), button('查询最新版本后，显式选择重新基线', async () => {
        draft.conflict = true; await refreshCurrent(false);
      }));
    }
    if (draft.conflict) renderConflict(form, draft);
    const fields = node('fieldset'); fields.disabled = state.busy || !!draft.pending; fields.setAttribute('aria-label', '完整题目分组');
    const groupRow = node('div', null, 'group-bar row'), picker = node('div', null, 'question-nav');
    draft.questions.forEach((question, index) => {
      const pick = button('第 ' + (index + 1) + ' 组 · 题号 ' + printedLabel(question), () => {
        draft.selectedQuestion = question.id; draft.selectedRegion = 0; draft.splitRegions.clear(); draft.drawing = false; renderJob();
      });
      pick.title = groupLabel(question, index);
      pick.setAttribute('aria-pressed', String(question.id === draft.selectedQuestion)); picker.append(pick);
    });
    groupRow.append(title, picker, button('新增题目组', () => {
      const question = { id:uuid(), questionNumber:null, subject:1, statement:'', studentAnswer:null, referenceAnswer:'', explanation:'', outcome:'unknown', evidence:'', regions:[] };
      draft.questions = [...draft.questions, question]; draft.selectedQuestion = question.id; draft.selectedRegion = 0; draft.splitRegions.clear(); changed(draft); renderJob();
    })); fields.append(groupRow);
    const question = selectedQuestion(draft);
    if (question) {
      const advanced = node('details'); advanced.open = !!draft.groupTools;
      advanced.ontoggle = () => { draft.groupTools = advanced.open; };
      advanced.append(node('summary', '分组操作'), node('p', '题目 UUID：' + question.id, 'mono')); fields.append(advanced);
      const groupActions = node('div', null, 'row');
      const index = draft.questions.indexOf(question);
      const moveGroup = offset => { const next = [...draft.questions]; [next[index], next[index + offset]] = [next[index + offset], next[index]]; draft.questions = next; changed(draft); renderJob(); };
      const up = button('题目组上移', () => moveGroup(-1)); up.disabled = index === 0;
      const down = button('题目组下移', () => moveGroup(1)); down.disabled = index === draft.questions.length - 1;
      groupActions.append(up, down, button('移除此题目组', () => {
        if (!confirm('从当前分组中移除此组？保存后会撤回它的当前关联，历史不删除。')) return;
        draft.questions = draft.questions.filter(item => item.id !== question.id); draft.selectedQuestion = draft.questions[0] ? draft.questions[0].id : null; draft.selectedRegion = 0; draft.splitRegions.clear(); changed(draft); renderJob();
      }, 'danger')); advanced.append(groupActions);
      renderMergeControls(advanced, draft, question);
      const columns = node('div', null, 'editor-layout'), text = node('div'), geometry = node('div');
      renderQuestionFields(text, draft, question); renderRegions(geometry, draft, question); columns.append(geometry, text); fields.append(columns);
    } else fields.append(node('p', '当前草稿没有题目组。可以新增；保存空集合会撤回全部当前分组。', 'notice warning'));
    const reason = node('label', '纠偏原因（写入版本记录）'), reasonInput = node('textarea'); reasonInput.id = 'revisionReason'; reasonInput.maxLength = 2000; reasonInput.required = true; reasonInput.placeholder = '填写修改原因，随新版本一起保存'; reasonInput.value = draft.reason; reasonInput.disabled = fields.disabled; reasonInput.oninput = () => { draft.reason = reasonInput.value; changed(draft); }; reason.append(reasonInput); form.append(fields);
    const footer = node('div', null, 'edit-footer row');
    const save = node('button', draft.pending ? '明确重发同一保存请求' : '保存完整分组', 'primary'); save.type = 'submit'; save.disabled = state.busy || draft.conflict || !['Succeeded','Failed'].includes(state.job.status);
    footer.append(reason);
    if (!draft.pending) footer.append(draftState);
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
    if (!regions.length || regions.length === question.regions.length || state.busy || draft.pending) return;
    if (!confirm('将勾选的 ' + regions.length + ' 个部分按现有阅读顺序拆为新 UUID？原组保留 UUID、题号和未勾选部分，新组题号留空。全部文字、作答与证据会复制供复核，但两组判定均重置为无法判断，不能沿用拆分前的结论。此操作只改草稿，保存后才生成新版本。')) return;
    const created = { ...question, id:uuid(), questionNumber:null, outcome:'unknown', regions:regions.map(region => ({ ...region })) };
    const remaining = { ...question, outcome:'unknown', regions:question.regions.filter(region => !draft.splitRegions.has(region)) };
    draft.questions = draft.questions.flatMap(item => item.id === question.id ? [remaining, created] : [item]);
    draft.selectedQuestion = created.id; draft.selectedRegion = 0; draft.selectedSource = created.regions[0].sourceId; draft.splitRegions.clear(); draft.drawing = false; changed(draft); renderJob();
    notice('message', '已拆为新 UUID ' + created.id + '，尚未保存。原组和新组保留全部文字，请分别核对题号、题干、作答、判定及部分顺序，再填写原因保存完整分组。');
  }
  function renderQuestionFields(root, draft, question) {
    root.append(node('h3', '题目与判定'));
    const numberLabel = node('label', '印刷题号'), numberInput = node('input');
    numberInput.id = 'questionNumber'; numberInput.type = 'text'; numberInput.maxLength = 64; numberInput.value = question.questionNumber ?? ''; numberInput.autocomplete = 'off'; numberInput.placeholder = '未知请留空';
    numberInput.oninput = () => {
      question.questionNumber = normalizedQuestionNumber(numberInput.value);
      const invalid = question.questionNumber !== null && (question.questionNumber.length > 64 || /[\u0000-\u001f\u007f-\u009f]/.test(question.questionNumber));
      numberInput.setCustomValidity(invalid ? '印刷题号最多 64 个字符，不能含控制字符。' : ''); numberInput.setAttribute('aria-invalid', String(invalid)); changed(draft);
    };
    numberInput.onblur = () => { numberInput.value = question.questionNumber ?? ''; };
    numberLabel.append(numberInput);
    const pair = node('div', null, 'editor-meta'), subjectLabel = node('label', '学科代码'), subject = node('input');
    subject.type = 'number'; subject.min = '1'; subject.max = '9'; subject.step = '1'; subject.required = true; subject.value = question.subject;
    subject.oninput = () => { question.subject = Number(subject.value); changed(draft); }; subjectLabel.append(subject);
    const outcomeLabel = node('label', '判定结论'), outcome = node('select');
    Object.entries(outcomeNames).forEach(([value, label]) => { const option = node('option', label); option.value = value; outcome.append(option); }); outcome.value = question.outcome;
    outcome.onchange = () => { question.outcome = outcome.value; changed(draft); }; outcomeLabel.append(outcome); pair.append(numberLabel, subjectLabel, outcomeLabel); root.append(pair);
    const textFields = node('div', null, 'grid text-fields'); root.append(textFields);
    const specs = [['statement','题干、选项与材料',8000],['studentAnswer','学生实际作答',8000],['referenceAnswer','参考答案',16000],['evidence','判断证据',16000],['explanation','解题过程',16000]];
    specs.forEach(([key, labelText, maximum]) => {
      const label = node('label', labelText), input = node('textarea'); input.maxLength = maximum; input.value = question[key] || ''; input.required = key === 'statement'; input.dataset.field = key;
      if (key === 'studentAnswer' || key === 'referenceAnswer') label.className = 'answer-field';
      input.oninput = () => { question[key] = key === 'studentAnswer' && !input.value.trim() ? null : input.value; changed(draft); }; label.append(input); textFields.append(label);
    });
    root.append(node('p', '请对照原图核实实际作答、参考答案、区域与判断证据。正确 / 错误结论必须具备真实作答及参考依据；未作答、看不清或材料不足应选“无法判断”。裁剪调整不会自行清空已保存的作答和判断。', 'muted'));
  }
  function renderRegions(root, draft, question) {
    const heading = node('div', null, 'spread'), source = node('select');
    source.setAttribute('aria-label', '框选所用原图'); heading.append(node('h3', '原图区域'), source); root.append(heading);
    state.job.sources.forEach((item, index) => { const option = node('option', '原图 ' + (index + 1) + ' · ' + item.width + ' × ' + item.height); option.value = item.id; source.append(option); });
    const selected = question.regions[draft.selectedRegion]; if (selected) draft.selectedSource = selected.sourceId;
    source.value = draft.selectedSource || ''; source.onchange = () => { draft.selectedSource = source.value; draft.selectedRegion = -1; draft.drawing = false; renderJob(); };
    const controls = node('div', null, 'row');
    controls.append(button('添加此原图完整区域', () => {
      if (question.regions.length >= 32 || !draft.selectedSource) return;
      question.regions = [...question.regions, { sourceId:draft.selectedSource, x0:0, y0:0, x1:1, y1:1 }]; draft.selectedRegion = question.regions.length - 1; changed(draft); renderJob();
    }));
    const draw = button(draft.drawing ? '取消框选' : selected ? '拖动重画选中区域' : '拖动新增区域', () => { draft.drawing = !draft.drawing; renderJob(); }); draw.setAttribute('aria-pressed', String(draft.drawing)); draw.disabled = !draft.selectedSource || (!selected && question.regions.length >= 32); controls.append(draw);
    if (selected) {
      const add = button('框选新区域', () => { draft.selectedRegion = -1; draft.drawing = true; renderJob(); });
      add.disabled = question.regions.length >= 32; controls.append(add);
    }
    const imageSource = state.job.sources.find(item => item.id === draft.selectedSource);
    if (imageSource) {
      const stage = node('div', null, 'stage' + (draft.drawing ? ' drawing' : '')); stage.setAttribute('role', 'group'); stage.setAttribute('aria-label', '当前原图和区域框，使用下方数字输入可完成同样操作');
      const image = node('img'); image.src = sourceUrl(state.job.id, imageSource.id); image.alt = sourceName(imageSource.id) + '，不可变原图，正向显示'; image.width = imageSource.width; image.height = imageSource.height; image.draggable = false; stage.append(image);
      image.onerror = () => { image.alt = '原图读取失败，不能框选。请检查会话并重新查询任务。'; draft.drawing = false; stage.classList.remove('drawing'); };
      question.regions.forEach((region, index) => { if (region.sourceId === imageSource.id) stage.append(regionBox(region, index, index === draft.selectedRegion)); });
      const scroll = node('div', null, 'image-scroll'); scroll.append(stage);
      wireRectangle(stage, image, draft, question); root.append(scroll, node('p', draft.drawing ? '框选模式：从起点拖到终点。Esc 取消。' : '先选择“拖动新增 / 重画区域”再框选，或使用下方数字输入。', 'muted'));
    }
    root.append(controls);
    const options = node('details'); options.open = !!draft.regionTools;
    options.ontoggle = () => { draft.regionTools = options.open; };
    options.append(node('summary', '区域顺序、坐标与拆分')); root.append(options);
    const splitHelp = node('p', '勾选要拆出的部分，至少保留 1 个部分在原组中。拆分保留原组 UUID，新组使用新 UUID；内容会完整复制，需人工复核。', 'muted'); splitHelp.id = 'splitHelp';
    const split = button('将勾选部分拆为新题目组', () => splitSelectedRegions(draft, question)); split.setAttribute('aria-describedby', 'splitHelp');
    const splitState = node('p', null, 'muted'); splitState.setAttribute('role', 'status');
    const updateSplit = () => {
      const count = question.regions.filter(region => draft.splitRegions.has(region)).length;
      split.disabled = !count || count === question.regions.length;
      splitState.textContent = '已勾选 ' + count + ' / ' + question.regions.length + ' 个部分。' + (count === question.regions.length && count ? '必须保留至少 1 个部分在原组。' : '');
    };
    updateSplit(); options.append(splitHelp, split, splitState);
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
    }); options.append(list);
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
    const history = node('details'); history.open = true; history.append(node('summary', '版本回执（' + state.job.versions.length + '）'));
    const list = node('ol', null, 'history-list');
    [...state.job.versions].sort((left, right) => right.version - left.version).forEach(version => {
      const li = node('li'); li.append(node('strong', 'v' + version.version + (version.version === state.job.version ? ' · 当前' : ' · 旧框和裁图已退役')), node('div', date(version.createdAt) + ' · ' + version.actor + ' · 模型 ' + version.model, 'muted'), node('p', version.reason || '未记录原因'), node('div', '请求键：' + version.requestKey, 'mono')); list.append(li);
    }); history.append(list);
    root.append(history);
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

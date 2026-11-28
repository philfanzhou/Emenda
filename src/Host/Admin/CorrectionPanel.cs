namespace Emenda.Host.Admin;

/// <summary>Explicit AI suggestion, preview/adoption and supplement controls; no service credentials.</summary>
internal static class CorrectionPanel
{
    public const string Script = """
<script>
window.renderEmendaCorrections = function(root, api) {
  const {job, selected} = api, base = '/admin/api/extractions/' + job.id;
  const element = (tag, text) => { const node = document.createElement(tag); if (text != null) node.textContent = text; return node; };
  const control = (text, action) => { const button = element('button', text); button.type = 'button'; button.onclick = () => run(action); return button; };
  const panel = element('details'); panel.append(element('summary', 'AI 纠偏与补图')); root.append(panel);
  let busy = false, generation = null, previous = null, previousRequest = null;
  async function run(action) {
    if (busy) return;
    busy = true; api.setBusy(true);
    try { await action(); }
    catch (error) { api.error(error.message); }
    finally { busy = false; api.setBusy(false); }
  }
  const form = element('form'), instruction = element('textarea'), scope = element('select'), sources = element('select');
  instruction.required = true; instruction.maxLength = 4000; instruction.rows = 3;
  instruction.placeholder = '例如：本题的解答延续到第二张原图上方，请补全，其他题不动。';
  instruction.setAttribute('aria-label', '纠偏说明'); scope.setAttribute('aria-label', '纠偏范围');
  for (const [value, title] of [['questions','当前题'],['regions','原图漏题'],['all','整卷重新分组']]) {
    if (value === 'questions' && !selected) continue;
    const option = element('option', title); option.value = value; scope.append(option);
  }
  sources.multiple = true; sources.setAttribute('aria-label', '提供给 AI 的原图');
  scope.onchange = () => { previous = null; previousRequest = null; };
  job.sources.forEach((source, i) => { const option = element('option', '原图 ' + (i + 1)); option.value = source.id; option.selected = true; sources.append(option); });
  const candidates = element('div');
  const generate = control('生成建议', async () => {
    if (!form.reportValidity()) return;
    if (scope.value === 'all' && !confirm('整卷重新分组可能新增、合并或撤回题目。现在只生成建议，采纳前不会修改当前结果。继续？')) return;
    const payload = { expectedVersion:job.version, instruction:instruction.value, scope:scope.value,
      questionIds:scope.value === 'questions' ? previousRequest?.questionIds || [selected.question.id] : [],
      sourceIds:[...sources.selectedOptions].map(option => option.value), regions:[], previousProposalId:previous };
    const identity = JSON.stringify(payload);
    if (!generation || generation.identity !== identity) generation = { identity, requestKey:crypto.randomUUID() };
    const response = await api.write('POST', base + '/proposals', { requestKey:generation.requestKey, ...payload });
    show([response.data]);
  });
  form.onsubmit = event => { event.preventDefault(); generate.click(); };
  form.append(scope, instruction, element('label', '原图'), sources, generate);
  panel.append(form, control('读取未处理建议', load), candidates);
  async function load() {
    const response = await api.read(base + '/proposals');
    if (!panel.isConnected) return;
    show(response.data);
  }
  function show(proposals) {
    candidates.replaceChildren();
    if (!proposals.length) candidates.append(element('p', '没有未处理建议。'));
    for (const proposal of proposals) {
      const article = element('article');
      article.append(element('h4', '基于 v' + proposal.expectedVersion + ' 的建议 · ' + proposal.status));
      if (proposal.request) article.append(element('p', proposal.request.instruction));
      if (proposal.status === 'Generating') {
        article.append(control('检查生成结果', load), control('重试此请求', async () => {
          const response = await api.write('POST', base + '/proposals', proposal.request); show([response.data]);
        })); candidates.append(article); continue;
      }
      if (proposal.status !== 'Pending') { candidates.append(article); continue; }
      const proposed = structuredClone(proposal.questions);
      const before = new Set(proposal.scope === 'all' ? job.questions.map(q => q.question.id) : proposal.questionIds);
      const after = new Set(proposed.map(q => q.id));
      article.append(element('p', '新增 ' + [...after].filter(id => !before.has(id)).length + ' 题，调整 ' + [...after].filter(id => before.has(id)).length + ' 题，撤回 ' + [...before].filter(id => !after.has(id)).length + ' 题。'));
      proposed.forEach(question => {
        const block = element('fieldset'); block.append(element('legend', (question.questionNumber || '未标题号') + ' · ' + ({incorrect:'错题',correct:'正确',unknown:'无法判断'}[question.outcome])));
        block.append(element('p', question.statement), element('p', '学生作答：' + (question.studentAnswer || '未识别')), element('p', '参考答案：' + question.referenceAnswer), element('p', '依据：' + question.evidence));
        question.regions.forEach((region, index) => {
          const source = job.sources.find(s => s.id === region.sourceId);
          if (!source) return;
          const frame = element('div'), image = element('img'), overlay = element('div');
          frame.style.cssText = 'position:relative;max-width:480px'; image.style.cssText = 'display:block;width:100%';
          image.src = base + '/sources/' + source.id + '?size=medium'; image.alt = '原图 ' + (job.sources.indexOf(source) + 1) + ' · 第 ' + (index + 1) + ' 部分';
          overlay.style.cssText = 'position:absolute;border:2px solid #2563eb;pointer-events:none';
          const draw = () => { overlay.style.left = region.x0 * 100 + '%'; overlay.style.top = region.y0 * 100 + '%'; overlay.style.width = (region.x1-region.x0) * 100 + '%'; overlay.style.height = (region.y1-region.y0) * 100 + '%'; };
          draw(); frame.append(image, overlay); block.append(frame);
          for (const [key, label] of [['x0','左'],['y0','上'],['x1','右'],['y1','下']]) {
            const line = element('label', label + '（%）'), input = element('input'); input.type = 'number'; input.min = '0'; input.max = '100'; input.step = 'any'; input.required = true; input.value = String(region[key] * 100);
            input.oninput = () => { region[key] = Number(input.value) / 100; draw(); }; line.append(input); block.append(line);
          }
        }); article.append(block);
      });
      const adoptionKey = crypto.randomUUID();
      article.append(control('采纳以上结果', async () => {
        if ([...article.querySelectorAll('input')].some(input => !input.reportValidity())) return;
        if (!confirm('采纳这些题目和框？未涉及题目保持原样；采纳不等于确认审核。')) return;
        await api.write('POST', base + '/proposals/' + proposal.id + '/adopt', { requestKey:adoptionKey, expectedVersion:proposal.expectedVersion, questions:proposed });
        await api.reload();
      }), control('继续说明', async () => {
        if (JSON.stringify(proposed) !== JSON.stringify(proposal.questions) && !confirm('继续说明会基于保存的建议，当前手调尚未提交。继续？')) return;
        previous = proposal.id; previousRequest = proposal.request; scope.value = proposal.scope;
        for (const option of sources.options) option.selected = !previousRequest.sourceIds.length || previousRequest.sourceIds.includes(option.value);
        instruction.focus();
      }),
      control('丢弃建议', async () => { await api.write('POST', base + '/proposals/' + proposal.id + '/discard'); await load(); }));
      candidates.append(article);
    }
  }
  if (selected && selected.itemId && !selected.itemDeleted && selected.question.outcome === 'incorrect') {
    const supplement = element('fieldset'); supplement.append(element('legend', '原题补图'));
    const pending = selected.supplement;
    if (pending) {
      supplement.append(element('p', pending.note));
      const files = element('input'); files.type = 'file'; files.accept = 'image/jpeg,image/png'; files.multiple = true; files.setAttribute('aria-label', '补充原图');
      const uploadKey = crypto.randomUUID();
      supplement.append(files, control('追加到原题', async () => {
        if (!files.files.length) return;
        const data = new FormData(); data.append('requestKey', uploadKey); data.append('expectedVersion', String(job.version)); data.append('itemId', selected.itemId);
        for (const file of files.files) data.append('images', file);
        await api.write('POST', base + '/questions/' + selected.question.id + '/supplements/' + pending.id + '/images', data);
        await api.reload();
      }));
    } else {
      const note = element('textarea'); note.maxLength = 2000; note.placeholder = '需要补充哪几行或哪张图'; note.setAttribute('aria-label', '补图说明'); const key = crypto.randomUUID();
      supplement.append(note, control('请求补图', async () => {
        if (!note.value.trim()) return;
        await api.write('POST', base + '/questions/' + selected.question.id + '/supplements', { requestKey:key, expectedVersion:job.version, itemId:selected.itemId, note:note.value });
        await api.reload();
      }));
    }
    panel.append(supplement);
  }
  load().catch(error => { if (panel.isConnected) api.error(error.message); });
};
</script>
""";
}

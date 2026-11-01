/* Settings > Connections (0.5.0 phase 7, CON-1): one place to add, test and enable every external service.
   Uses api('/api/v1/connections…') from app.js until the generated client includes these operations.
   Secret values never come back from the server: a saved secret shows as a "saved" placeholder and is kept
   unless a new value is typed. Every text from the server is escaped before it reaches innerHTML. */
(() => {
  const $ = id => document.getElementById(id);
  const text = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const when = iso => iso ? new Date(iso).toLocaleString() : 'never';
  const request = (path, options = {}) => api('/api/v1/connections' + path, { timeoutMs: 25000, noMutationRefresh: true, cache: 'no-store', ...options });
  const send = (path, method, body) => request(path, { method, body: JSON.stringify(body) });
  const state = { data: null, editing: null, loading: null, notice: '' };
  const importNote = 'Create entries adds the stored library to the catalogue through the import pipeline; Imports shows the progress and any rows that need review.';

  /* Phase 9: after a plan is stored, "Create entries" runs it through the import pipeline (POST /api/v1/imports/suwayomi-plan). */
  function offerCreateEntries(output, plan) {
    if (plan && plan.status === 'imported') return;
    const button = document.createElement('button');
    button.type = 'button'; button.textContent = 'Create entries';
    const note = document.createElement('div'); note.className = 'muted';
    button.onclick = async () => {
      button.disabled = true; note.textContent = 'Queueing…';
      try {
        const queued = await api('/api/v1/imports/suwayomi-plan', { method: 'POST', body: '{}', timeoutMs: 25000 });
        note.textContent = `Creating entries as import ${queued.import_id || ''}${queued.task_id ? ` (task ${queued.task_id})` : ''}; see Imports.`;
      } catch (error) {
        note.textContent = 'Create entries failed: ' + error.message + (/not found|404/i.test(error.message) ? ' The library read may still be running; try again when its task finished.' : '');
        button.disabled = false;
      }
    };
    output.append(document.createElement('br'), button, note);
  }

  /* CON-5: a change answers with an exact notice of what happens next (source host restart, feed reconnect, rollback note). */
  function noticeFrom(answer) {
    state.notice = answer && typeof answer.notice === 'string' ? answer.notice : '';
    if (state.notice) toast(state.notice);
  }

  function planSummary(data) {
    const plan = data && data.plan;
    if (!plan) return 'Not imported yet.';
    const counts = plan.counts || {};
    return `Imported ${Number(counts.total) || 0} manga from ${Number(counts.sources) || 0} sources on ${when(plan.saved_at)} (${plan.status || 'stored'}).`;
  }

  const typeOf = name => (state.data?.types || []).find(type => type.type === name);
  const connectionOf = id => (state.data?.connections || []).find(item => item.id === id);

  function details(value) {
    if (!value || typeof value !== 'object') return '';
    const rows = Object.entries(value).map(([key, item]) => `<li><b>${text(key)}</b>: ${text(typeof item === 'string' ? item : JSON.stringify(item))}</li>`);
    return rows.length ? `<ul class="connectionDetails">${rows.join('')}</ul>` : '';
  }

  function showResult(target, result) {
    target.hidden = false;
    target.innerHTML = `<b>${result.ok ? 'Passed' : 'Failed'}</b>: ${text(result.message)}${details(result.details)}`;
  }

  function fieldInput(field, value) {
    const id = 'connectionField_' + field.key;
    const required = field.required ? ' <span aria-hidden="true">*</span>' : '';
    const help = field.help ? `<small class="muted">${text(field.help)}</small>` : '';
    const attributes = `id="${text(id)}" data-field="${text(field.key)}" data-kind="${text(field.kind)}"`;
    let input;
    if (field.kind === 'secret') {
      const saved = value && typeof value === 'object' && value.set;
      input = `<input ${attributes} type="password" autocomplete="new-password" maxlength="4096" placeholder="${saved ? 'saved' : ''}"/>`;
    } else if (field.kind === 'boolean') {
      const current = value == null ? '' : String(value);
      input = `<select ${attributes}><option value=""${current === '' ? ' selected' : ''}>Default${field.default ? ' (' + text(field.default) + ')' : ''}</option>` +
        `<option value="true"${current === 'true' ? ' selected' : ''}>On</option><option value="false"${current === 'false' ? ' selected' : ''}>Off</option></select>`;
    } else {
      const type = field.kind === 'integer' ? 'type="number" step="1"' : field.kind === 'url' ? 'type="url"' : 'type="text"';
      const placeholder = field.default ? text(field.default) : field.kind === 'url' ? 'https://…' : '';
      input = `<input ${attributes} ${type} maxlength="4096" placeholder="${placeholder}" value="${text(value ?? '')}"/>`;
    }
    return `<label for="${text(id)}">${text(field.label)}${required}${input}${help}</label>`;
  }

  function renderForm() {
    const editing = state.editing ? connectionOf(state.editing) : null;
    const select = $('connectionAddType');
    select.disabled = !!editing;
    if (editing) select.value = editing.type;
    const type = typeOf(select.value);
    $('connectionFormTitle').textContent = editing ? `Change ${editing.label}` : 'Add a connection';
    $('connectionAddSave').textContent = editing ? 'Save changes' : 'Add connection';
    $('connectionEditCancel').hidden = !editing;
    $('connectionAddResult').hidden = true;
    if (!type) {
      $('connectionAddPurpose').textContent = 'No connection types are available.';
      $('connectionAddForm').innerHTML = '';
      $('connectionAddSave').disabled = $('connectionAddTest').disabled = true;
      return;
    }
    const taken = !type.multiple && !editing && (state.data.connections || []).some(item => item.type === type.type && item.source === 'config');
    $('connectionAddPurpose').textContent = type.purpose + (taken ? ' This type allows one connection; change the existing one instead.' : '');
    $('connectionAddSave').disabled = taken;
    $('connectionAddTest').disabled = false;
    const values = editing?.values || {};
    $('connectionAddForm').innerHTML = `<label for="connectionLabel">Name<input id="connectionLabel" maxlength="100" placeholder="${text(type.label)}" value="${text(editing?.label ?? '')}"/></label>` +
      (type.fields || []).map(field => fieldInput(field, values[field.key])).join('');
  }

  /* Empty inputs are left out on add (field defaults apply); on a change an empty secret keeps the saved one ({set:true})
     and an emptied plain field is cleared (null). */
  function collect() {
    const values = {};
    const editing = !!state.editing;
    for (const input of $('connectionAddForm').querySelectorAll('[data-field]')) {
      const key = input.dataset.field, kind = input.dataset.kind;
      const raw = kind === 'secret' ? input.value : input.value.trim();
      if (kind === 'secret') { if (raw) values[key] = raw; else if (editing) values[key] = { set: true }; continue; }
      if (!raw) { if (editing) values[key] = null; continue; }
      values[key] = kind === 'boolean' ? raw === 'true' : raw;
    }
    return values;
  }

  async function testForm() {
    const target = $('connectionAddResult');
    target.hidden = false; target.textContent = 'Testing…';
    try {
      const body = { type: $('connectionAddType').value, values: collect() };
      if (state.editing) body.id = state.editing;
      showResult(target, await send('/test', 'POST', body));
    } catch (error) { target.textContent = 'Test failed: ' + error.message; }
  }

  async function saveForm() {
    const label = $('connectionLabel')?.value.trim() || null;
    try {
      const answer = state.editing
        ? await send('/' + encodeURIComponent(state.editing), 'PUT', { revision: state.data.revision, label, values: collect() })
        : await send('', 'POST', { revision: state.data.revision, type: $('connectionAddType').value, label, values: collect() });
      state.editing = null;
      toast('Connection saved.');
      noticeFrom(answer);
      await loadConnectionsPane(true);
    } catch (error) {
      toast(error.message);
      if (error.status_code === 409) await loadConnectionsPane(true);
    }
  }

  function renderList() {
    const items = state.data.connections || [];
    const target = $('connectionsList');
    if (!items.length) { target.textContent = 'No connections yet. Add one above.'; return; }
    target.innerHTML = '<table class="dataTable"><thead><tr><th>Name</th><th>Type</th><th>Enabled</th><th>Last test</th><th></th></tr></thead><tbody>' +
      items.map(item => {
        const type = typeOf(item.type);
        const fromSettings = item.source === 'settings';
        const last = item.last_test ? `${item.last_test.ok ? 'Passed' : 'Failed'} ${text(when(item.last_test.at))}<br/><small class="muted">${text(item.last_test.message)}</small>` : 'never';
        const enabled = `<label><input type="checkbox" data-enable="${text(item.id)}"${item.enabled ? ' checked' : ''}${item.editable ? '' : ' disabled'}/> ${item.enabled ? 'On' : 'Off'}</label>`;
        const actions = `<button type="button" data-test="${text(item.id)}"${item.type_known ? '' : ' disabled'}>Test</button>` +
          (item.type === 'suwayomi' && item.type_known ? `<button type="button" data-import="${text(item.id)}">Import library</button>` : '') +
          (item.editable ? `<button type="button" data-edit="${text(item.id)}"${item.type_known ? '' : ' disabled'}>Change</button><button type="button" class="dangerAction" data-delete="${text(item.id)}">Delete</button>` : '');
        return `<tr><td>${text(item.label)}${fromSettings ? '<br/><small class="muted">From Settings; change it on the Application settings tab.</small>' : ''}</td>` +
          `<td>${text(type?.label || item.type)}${item.type_known ? '' : ' <small class="muted">(not available in this version)</small>'}</td><td>${enabled}</td><td>${last}</td>` +
          `<td>${actions}<div class="notice" hidden data-result="${text(item.id)}" role="status"></div></td></tr>`;
      }).join('') + '</tbody></table>';
    const result = id => [...target.querySelectorAll('[data-result]')].find(node => node.dataset.result === id);
    target.querySelectorAll('[data-enable]').forEach(box => box.onchange = async () => {
      try { noticeFrom(await send('/' + encodeURIComponent(box.dataset.enable), 'PUT', { revision: state.data.revision, enabled: box.checked })); }
      catch (error) { toast(error.message); }
      await loadConnectionsPane(true);
    });
    target.querySelectorAll('[data-test]').forEach(button => button.onclick = async () => {
      const id = button.dataset.test, output = result(id);
      button.disabled = true; output.hidden = false; output.textContent = 'Testing…';
      try { showResult(output, await send('/' + encodeURIComponent(id) + '/test', 'POST', {})); }
      catch (error) { output.textContent = 'Test failed: ' + error.message; }
      finally { button.disabled = false; }
    });
    /* CON-3 one-time import of the main Suwayomi library: shows the stored plan, and queues a (re)read as a task. */
    target.querySelectorAll('[data-import]').forEach(button => button.onclick = async () => {
      const id = button.dataset.import, output = result(id), path = '/' + encodeURIComponent(id) + '/import';
      button.disabled = true; output.hidden = false; output.textContent = 'Reading the stored import…';
      try {
        const current = await request(path);
        let force = false;
        if (current.plan) {
          if (!window.confirm(planSummary(current) + ' Read the library again and replace the stored import?')) {
            output.innerHTML = `${text(planSummary(current))}<br/><small class="muted">${text(importNote)}</small>`;
            offerCreateEntries(output, current.plan);
            return;
          }
          force = true;
        }
        const queued = await send(path, 'POST', { force });
        output.innerHTML = `Import queued as task ${text(queued.task_id)} (see Tasks). ${text(planSummary(current))}<br/><small class="muted">${text(importNote)}</small>`;
        offerCreateEntries(output, null);
      } catch (error) { output.textContent = 'Import failed: ' + error.message; }
      finally { button.disabled = false; }
    });
    target.querySelectorAll('[data-edit]').forEach(button => button.onclick = () => {
      state.editing = button.dataset.edit; renderForm();
      $('connectionAddForm').scrollIntoView({ block: 'nearest' });
    });
    target.querySelectorAll('[data-delete]').forEach(button => button.onclick = async () => {
      const item = connectionOf(button.dataset.delete);
      if (!window.confirm(`Delete the connection "${item?.label || button.dataset.delete}"? Features that use it stop working until you add it again.`)) return;
      try { noticeFrom(await request('/' + encodeURIComponent(button.dataset.delete) + '?revision=' + encodeURIComponent(state.data.revision), { method: 'DELETE' })); }
      catch (error) { toast(error.message); }
      if (state.editing === button.dataset.delete) state.editing = null;
      await loadConnectionsPane(true);
    });
  }

  function renderTypes() {
    const types = state.data.types || [];
    const select = $('connectionAddType');
    const current = select.value;
    select.innerHTML = types.map(type => `<option value="${text(type.type)}">${text(type.label)}</option>`).join('');
    select.value = types.some(type => type.type === current) ? current : (types[0]?.type || '');
    $('connectionTypes').innerHTML = types.length ? '<table class="dataTable"><thead><tr><th>Type</th><th>What it is for</th><th>Connections</th></tr></thead><tbody>' +
      types.map(type => `<tr><td>${text(type.label)}</td><td>${text(type.purpose)}</td><td>${type.multiple ? 'Several' : 'One'}</td></tr>`).join('') + '</tbody></table>' : 'No connection types are available.';
  }

  async function loadConnectionsPane(force = false) {
    if (!$('settingsConnectionsPane')) return;
    if (state.loading && !force) return state.loading;
    state.loading = (async () => {
      try {
        state.data = await request('');
        if (state.editing && !connectionOf(state.editing)?.editable) state.editing = null;
        renderTypes(); renderForm(); renderList();
        $('connectionsState').textContent = 'Add, test and enable external services. Secrets are stored encrypted and never shown again.' +
          (state.notice ? ' Last change: ' + state.notice : '');
      } catch (error) { $('connectionsState').textContent = 'Unable to load connections: ' + error.message; }
      finally { state.loading = null; }
    })();
    return state.loading;
  }
  window.loadConnectionsPane = loadConnectionsPane;

  function bind() {
    if (!$('settingsConnectionsPane')) return;
    $('connectionsRefresh').onclick = () => loadConnectionsPane(true);
    $('connectionAddType').onchange = () => { state.editing = null; renderForm(); };
    $('connectionAddTest').onclick = testForm;
    $('connectionAddSave').onclick = saveForm;
    $('connectionEditCancel').onclick = () => { state.editing = null; renderForm(); };
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', bind); else bind();
})();

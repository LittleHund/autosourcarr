/* Settings > Security (0.5.0 phase 5): who is signed in, admin token, keys, accounts (owner) and the security log.
   Everything goes through the generated /api/v1 client (apiV1 from app.js). */
(() => {
  const $ = id => document.getElementById(id);
  const text = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const when = seconds => seconds ? new Date(seconds * 1000).toLocaleString() : 'never';
  const client = () => apiV1({ timeoutMs: 8000, noMutationRefresh: true, cache: 'no-store' });
  const state = { token: '', shown: false, me: null, loading: null };

  function renderToken() {
    $('securityAdminToken').textContent = state.token ? (state.shown ? state.token : state.token.slice(0, 8) + '…') : 'not available';
    $('securityShowToken').textContent = state.shown ? 'Hide' : 'Show';
    $('securityRegenerateToken').hidden = state.me?.role !== 'owner';
  }

  function renderKeys(keys) {
    const live = keys.filter(key => !key.revoked);
    $('securityKeys').innerHTML = live.length ? '<table class="dataTable"><thead><tr><th>Name</th><th>Kind</th><th>Rights</th><th>Starts with</th><th>Last used</th><th></th></tr></thead><tbody>' +
      live.map(key => `<tr><td>${text(key.name)}</td><td>${text(key.kind)}</td><td>${text(key.role)}</td><td><code>${text(key.prefix)}…</code></td><td>${text(when(key.last_used))}</td><td><button type="button" class="dangerAction" data-revoke="${text(key.id)}">Revoke</button></td></tr>`).join('') +
      '</tbody></table>' : 'No keys yet.';
    $('securityKeys').querySelectorAll('[data-revoke]').forEach(button => button.onclick = async () => {
      if (!window.confirm('Revoke this key? Devices or scripts using it stop working at once.')) return;
      try { await client().revokeKey(button.dataset.revoke); await loadSecurityPane(true); } catch (error) { toast(error.message); }
    });
  }

  function renderAccounts(items) {
    $('securityAccounts').innerHTML = '<table class="dataTable"><thead><tr><th>Name</th><th>Role</th><th>Sign-in</th><th>Created</th><th></th></tr></thead><tbody>' +
      items.map(account => `<tr><td>${text(account.name)}</td><td>${text(account.role)}</td><td>${text(account.source)}</td><td>${text(when(account.created))}</td><td>${account.role === 'owner' ? '' : `<button type="button" class="dangerAction" data-remove="${text(account.id)}">Remove</button>`}</td></tr>`).join('') + '</tbody></table>';
    $('securityAccounts').querySelectorAll('[data-remove]').forEach(button => button.onclick = async () => {
      if (!window.confirm('Remove this account? It is signed out everywhere.')) return;
      try { await client().deleteAccount(button.dataset.remove); await loadSecurityPane(true); } catch (error) { toast(error.message); }
    });
  }

  async function loadEvents() {
    const kind = $('securityEventKind').value;
    try {
      const data = await api('/api/v1/security/events?limit=200' + (kind ? '&kind=' + encodeURIComponent(kind) : ''), { timeoutMs: 8000, noMutationRefresh: true, cache: 'no-store' });
      const select = $('securityEventKind');
      if (select.options.length <= 1) for (const item of data.kinds || []) select.append(new Option(item, item));
      const items = data.items || [];
      $('securityEvents').innerHTML = items.length ? '<table class="dataTable"><thead><tr><th>Time</th><th>Event</th><th>Who</th><th>Address</th><th>Detail</th></tr></thead><tbody>' +
        items.map(row => `<tr><td>${text(new Date(row.at).toLocaleString())}</td><td>${text(row.kind)}</td><td>${text(row.user)}</td><td>${text(row.address)}</td><td>${text(row.detail)}</td></tr>`).join('') + '</tbody></table>' : 'No events.';
    } catch (error) { $('securityEvents').textContent = 'Unable to load the security log: ' + error.message; }
  }

  async function loadSecurityPane(force = false) {
    if (state.loading && !force) return state.loading;
    state.loading = (async () => {
      try {
        state.me = await client().getMe();
        $('securityWho').textContent = `Signed in as ${state.me.user || 'unknown'} (${state.me.role}, ${state.me.via}).`;
        $('securitySignOut').hidden = state.me.via !== 'session';
        const keys = await client().getKeys();
        state.token = keys.admin_token || '';
        renderToken(); renderKeys(keys.keys || []);
        $('securityAccountsPanel').hidden = state.me.role !== 'owner';
        if (state.me.role === 'owner') renderAccounts((await client().getAccounts()).items || []);
        await loadEvents();
      } catch (error) { $('securityWho').textContent = 'Unable to load security settings: ' + error.message; }
      finally { state.loading = null; }
    })();
    return state.loading;
  }
  window.loadSecurityPane = loadSecurityPane;

  function bind() {
    if (!$('settingsSecurityPane')) return;
    $('securityRefresh').onclick = () => loadSecurityPane(true);
    $('securityShowToken').onclick = () => { state.shown = !state.shown; renderToken(); };
    $('securityEventKind').onchange = loadEvents;
    $('securitySignOut').onclick = async () => { try { await client().logout(); } finally { location.href = '/login'; } };
    $('securityRegenerateToken').onclick = async () => {
      if (!window.confirm('Replace the admin token? Scripts using the old token stop working at once.')) return;
      try { state.token = (await client().regenerateAdminToken()).admin_token || ''; state.shown = true; renderToken(); } catch (error) { toast(error.message); }
    };
    $('securityCreateKey').onclick = async () => {
      const [kind, role] = $('securityKeyKind').value.split(':');
      const name = $('securityKeyName').value.trim();
      if (!name) { toast('Give the key a name, for example the device it is for.'); return; }
      try {
        const key = await client().createKey({ name, kind, role: role || (kind === 'reader' ? 'reader' : 'admin') });
        const note = $('securityNewKey'); note.hidden = false;
        note.innerHTML = `Key <b>${text(key.name)}</b> created. Copy it now; it is not shown again: <code>${text(key.secret)}</code>`;
        $('securityKeyName').value = ''; await loadSecurityPane(true);
      } catch (error) { toast(error.message); }
    };
    $('securityCreateUser').onclick = async () => {
      const username = $('securityUserName').value.trim(), password = $('securityUserPassword').value;
      try {
        await client().createAccount({ username, password, role: $('securityUserRole').value });
        $('securityUserName').value = ''; $('securityUserPassword').value = ''; await loadSecurityPane(true);
      } catch (error) { toast(error.message); }
    };
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', bind); else bind();
})();

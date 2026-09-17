/* SSO Authentication — Plugin Config Page
 * Loaded by Jellyfin's SPA via data-controller="__plugin/SSO Authentication.js".
 * Must be an ES module; default export receives the view element. */

var API = '/sso/admin';
var _libraries = [], _seenGroups = [], _config = null;

export default function (view) {
    load(view).catch(function (e) { Dashboard.alert('Failed to load SSO config: ' + e.message); });

    setupTabs(view);

    view.querySelector('#Save').addEventListener('click', function () {
        save(view);
    });

    view.querySelector('#AddMapping').addEventListener('click', function () {
        var c = view.querySelector('#GroupMappings');
        c.appendChild(buildRow(view, {}, c.children.length));
        renumber(view);
    });

    view.querySelector('#AddLink').addEventListener('click', function () {
        var pid = getVal(view, 'LinkProviderId');
        var sub = getVal(view, 'LinkSub').trim();
        var uid = getVal(view, 'LinkUserId');
        if (!pid || !sub || !uid) { Dashboard.alert('Provider, user and subject are all required.'); return; }
        apiPost(API + '/links', { ProviderId: pid, Sub: sub, JellyfinUserId: uid })
            .then(function () {
                setVal(view, 'LinkSub', '');
                return populateLinkForm(view);
            })
            .then(function () { renderLinks(view); })
            .catch(function (e) { Dashboard.alert(e.message); });
    });
}

// ─── Tabs ──────────────────────────────────────────────────────────────────────

function setupTabs(view) {
    var tabs = Array.from(view.querySelectorAll('.sso-tab'));
    var panels = Array.from(view.querySelectorAll('.sso-panel'));
    var saveBar = view.querySelector('#SaveBar');

    tabs.forEach(function (tab) {
        tab.addEventListener('click', function () {
            var name = tab.dataset.tab;
            tabs.forEach(function (t) { t.classList.toggle('is-active', t === tab); });
            panels.forEach(function (p) { p.hidden = (p.dataset.panel !== name); });
            // Save applies to Provider + Group Mappings only.
            saveBar.hidden = (name === 'links');
        });
    });
}

// ─── Load ────────────────────────────────────────────────────────────────────

function load(view) {
    return Promise.all([
        apiGet(API + '/config'),
        apiGet(API + '/libraries'),
        apiGet(API + '/seen-groups'),
    ]).then(function (r) {
        _config = r[0]; _libraries = r[1]; _seenGroups = r[2];
        var p = (_config.Providers && _config.Providers[0]) || {};
        setChk(view, 'ProviderEnabled', !!p.Enabled);
        setVal(view, 'Authority', p.Authority || '');
        setVal(view, 'ClientId', p.ClientId || '');
        setVal(view, 'GroupsClaim', p.GroupsClaim || '');
        setChk(view, 'GroupsFromUserInfo', !!p.GroupsFromUserInfo);
        if (p.Id) view.querySelector('#SsoLoginUrl').textContent = '/sso/oidc/start/' + p.Id;
        renderMappings(view, _config.GroupMappings || []);
        populateLinkForm(view);
        renderLinks(view);
    });
}

function populateLinkForm(view) {
    // Provider dropdown
    var provSel = view.querySelector('#LinkProviderId');
    provSel.innerHTML = (_config.Providers || []).map(function (p) {
        return '<option value="' + escAttr(p.Id) + '">' + escHtml(p.Name || p.Id) + '</option>';
    }).join('');

    // Jellyfin user dropdown — only unlinked users (linked ones can't be re-linked anyway)
    return apiGet(API + '/users').then(function (users) {
        var userSel = view.querySelector('#LinkUserId');
        userSel.innerHTML = users.map(function (u) {
            return '<option value="' + escAttr(u.Id) + '"' + (u.Linked ? ' disabled' : '') + '>'
                + escHtml(u.Name) + (u.Linked ? ' (already linked)' : '') + '</option>';
        }).join('');
    });
}

// ─── Save ────────────────────────────────────────────────────────────────────

function save(view) {
    var p = (_config && _config.Providers && _config.Providers[0]) || {};
    var provider = {
        Id: p.Id || newId(),
        Name: 'Default',
        Enabled: getChk(view, 'ProviderEnabled'),
        Authority: getVal(view, 'Authority'),
        ClientId: getVal(view, 'ClientId'),
        ClientType: 0,
        Scopes: ['openid', 'profile', 'email'],
        GroupsClaim: getVal(view, 'GroupsClaim') || 'groups',
        GroupsFromUserInfo: getChk(view, 'GroupsFromUserInfo'),
    };
    var cfg = {
        Providers: [provider],
        GroupMappings: collectMappings(view),
        Links: (_config && _config.Links) || [],
        SeenGroups: (_config && _config.SeenGroups) || [],
    };
    apiPost(API + '/config', cfg).then(function () {
        _config = cfg;
        view.querySelector('#SsoLoginUrl').textContent = '/sso/oidc/start/' + provider.Id;
        Dashboard.processPluginConfigurationUpdateResult();
    }).catch(function (e) { Dashboard.alert('Save failed: ' + e.message); });
}

// ─── Mapping table ────────────────────────────────────────────────────────────

function renderMappings(view, mappings) {
    var c = view.querySelector('#GroupMappings');
    c.innerHTML = '';
    mappings.forEach(function (m, i) { c.appendChild(buildRow(view, m, i)); });
}

function buildRow(view, m, i) {
    var card = document.createElement('div');
    card.className = 'sso-mapping paperList';

    var listId = 'sso-gl-' + i;
    var dlOpts = _seenGroups.map(function (g) {
        return '<option value="' + escAttr(g) + '"></option>';
    }).join('');

    // Match Jellyfin's native folderAccess markup; emby-checkbox upgrade injects the
    // checkboxLabel/checkboxOutline spans automatically once inserted into the DOM.
    var libsHtml = _libraries.map(function (lib) {
        var checked = (m.LibraryIds || []).indexOf(lib.ItemId) !== -1 ? ' checked' : '';
        return '<div class="sectioncheckbox"><label><input type="checkbox" is="emby-checkbox" class="sso-lib-cb" value="'
            + escAttr(lib.ItemId) + '"' + checked + ' /><span>' + escHtml(lib.Name) + '</span></label></div>';
    }).join('');

    var adminChecked = m.IsAdministrator ? ' checked' : '';

    // Built as innerHTML so Jellyfin's emby-* custom elements auto-upgrade on DOM insertion.
    card.innerHTML =
        '<h3 class="checkboxListLabel sso-mapping-title">Group ' + (i + 1) + '</h3>' +
        '<div class="sso-mapping-head">' +
            '<div class="inputContainer sso-group-field">' +
                '<input type="text" is="emby-input" class="sso-group-input" label="Group name" list="' + listId +
                    '" value="' + escAttr(m.Group || '') + '" />' +
                '<datalist id="' + listId + '">' + dlOpts + '</datalist>' +
            '</div>' +
            '<div class="checkboxContainer sso-admin-toggle"><label><input type="checkbox" is="emby-checkbox" class="sso-admin-check"' +
                adminChecked + ' /><span>Admin (all libraries)</span></label></div>' +
            '<button type="button" is="emby-button" class="raised sso-remove-btn"><span>Remove</span></button>' +
        '</div>' +
        '<div class="folderAccess sso-libs-section">' +
            '<h3 class="checkboxListLabel">Libraries</h3>' +
            '<div class="checkboxList sso-libs-grid">' + libsHtml + '</div>' +
        '</div>';

    var ac = card.querySelector('.sso-admin-check');
    var libsSection = card.querySelector('.sso-libs-section');

    // Admin = all folders → hide the per-library list when admin is checked.
    function syncAdmin() {
        libsSection.style.display = ac.checked ? 'none' : '';
    }
    ac.addEventListener('change', syncAdmin);
    syncAdmin();

    card.querySelector('.sso-remove-btn').addEventListener('click', function () {
        card.remove();
        renumber(view);
    });

    return card;
}

function renumber(view) {
    Array.from(view.querySelectorAll('.sso-mapping .sso-mapping-title')).forEach(function (t, idx) {
        t.textContent = 'Group ' + (idx + 1);
    });
}

function collectMappings(view) {
    return Array.from(view.querySelectorAll('.sso-mapping')).map(function (card) {
        var gi = card.querySelector('.sso-group-input');
        var ac = card.querySelector('.sso-admin-check');
        var g = gi ? gi.value.trim() : '';
        if (!g) return null;
        var libIds = Array.from(card.querySelectorAll('.sso-lib-cb'))
            .filter(function (cb) { return cb.checked; })
            .map(function (cb) { return cb.value; });
        return {
            Group: g,
            LibraryIds: libIds,
            IsAdministrator: ac ? ac.checked : false,
        };
    }).filter(Boolean);
}

// ─── Links ───────────────────────────────────────────────────────────────────

function renderLinks(view) {
    apiGet(API + '/links').then(function (links) {
        var c = view.querySelector('#Links'); c.innerHTML = '';
        if (!links || !links.length) { c.textContent = 'No manual links configured.'; return; }
        var t = document.createElement('table'); t.className = 'sso-links-table';
        t.innerHTML = '<thead><tr><th>User</th><th>Provider</th><th>Subject</th><th></th></tr></thead>';
        var tb = document.createElement('tbody');
        links.forEach(function (l) {
            var tr = document.createElement('tr');
            [l.Username, l.ProviderId].forEach(function (v) {
                var td = document.createElement('td'); td.textContent = v; tr.appendChild(td);
            });
            var tds = document.createElement('td');
            var code = document.createElement('code'); code.textContent = l.Sub; tds.appendChild(code); tr.appendChild(tds);
            var tdb = document.createElement('td');
            var btn = document.createElement('button', { is: 'emby-button' });
            btn.setAttribute('is', 'emby-button');
            btn.type = 'button'; btn.className = 'raised'; btn.innerHTML = '<span>Remove</span>';
            btn.addEventListener('click', function () {
                apiDelete(API + '/links/' + l.JellyfinUserId)
                    .then(function () { return populateLinkForm(view); })
                    .then(function () { renderLinks(view); })
                    .catch(function (e) { Dashboard.alert(e.message); });
            });
            tdb.appendChild(btn); tr.appendChild(tdb); tb.appendChild(tr);
        });
        t.appendChild(tb); c.appendChild(t);
    });
}

// ─── HTTP helpers ─────────────────────────────────────────────────────────────

function authHeaders() {
    return { 'Content-Type': 'application/json', 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' };
}
function apiGet(url) {
    return fetch(url, { headers: authHeaders() }).then(checkOk).then(function (r) { return r.json(); });
}
function apiPost(url, body) {
    return fetch(url, { method: 'POST', headers: authHeaders(), body: JSON.stringify(body) }).then(checkOk);
}
function apiDelete(url) {
    return fetch(url, { method: 'DELETE', headers: authHeaders() }).then(checkOk);
}
function checkOk(r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r; }

// ─── DOM helpers ──────────────────────────────────────────────────────────────

function getVal(view, id) { var e = view.querySelector('#' + id); return e ? e.value : ''; }
function setVal(view, id, v) { var e = view.querySelector('#' + id); if (e) e.value = v; }
function getChk(view, id) { var e = view.querySelector('#' + id); return e ? e.checked : false; }
function setChk(view, id, v) { var e = view.querySelector('#' + id); if (e) e.checked = v; }
function newId() { return Math.random().toString(36).slice(2) + Date.now().toString(36); }
function escHtml(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
function escAttr(s) { return escHtml(s).replace(/"/g, '&quot;'); }

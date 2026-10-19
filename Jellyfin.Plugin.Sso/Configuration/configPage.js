/* SSO Authentication — Plugin Config Page
 * Loaded by Jellyfin's SPA via data-controller="__plugin/SSO Authentication.js".
 * Must be an ES module; default export receives the view element. */

var API = '/sso/admin';
var _libraries = [], _seenGroups = [], _users = [], _config = null;

export default function (view) {
    load(view).catch(function (e) { Dashboard.alert('Failed to load SSO config: ' + e.message); });

    setupTabs(view);

    view.querySelector('#Save').addEventListener('click', function () {
        save(view);
    });

    view.querySelector('#AddProvider').addEventListener('click', function () {
        var c = view.querySelector('#Providers');
        c.appendChild(buildProviderCard({ Id: newId(), Enabled: false }, c.children.length));
        retitleProviders(view);
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
        renderProviders(view, _config.Providers || []);
        setChk(view, 'SignOutOfIdentityProvider', !!_config.SignOutOfIdentityProvider);
        setChk(view, 'AddLoginButtonToBranding', _config.AddLoginButtonToBranding !== false);
        setChk(view, 'UseProviderLogos', _config.UseProviderLogos !== false);
        setChk(view, 'InjectLoginButtonIntoForm', !!_config.InjectLoginButtonIntoForm);
        setChk(view, 'AllowSelfServiceLinking', !!_config.AllowSelfServiceLinking);
        setChk(view, 'PreserveAdminPermissions', !!_config.PreserveAdminPermissions);
        renderMappings(view, _config.GroupMappings || []);
        renderLinks(view);
        return populateUsers(view);
    });
}

// Users are needed twice: the manual-link dropdown and the exclude-from-sync list.
function populateUsers(view) {
    return apiGet(API + '/users').then(function (users) {
        _users = users;
        populateLinkForm(view);
        renderExcluded(view);
    });
}

function renderExcluded(view) {
    var excluded = _config.SyncExcludedUserIds || [];
    view.querySelector('#SyncExcluded').innerHTML = _users.map(function (u) {
        var checked = excluded.some(function (id) { return sameId(id, u.Id); }) ? ' checked' : '';
        return '<div class="sectioncheckbox"><label><input type="checkbox" is="emby-checkbox" class="sso-excl-cb" value="'
            + escAttr(u.Id) + '"' + checked + ' /><span>' + escHtml(u.Name) + '</span></label></div>';
    }).join('');
}

function collectExcluded(view) {
    return Array.from(view.querySelectorAll('.sso-excl-cb'))
        .filter(function (cb) { return cb.checked; })
        .map(function (cb) { return cb.value; });
}

// Jellyfin hands out ids with and without dashes depending on the endpoint.
function sameId(a, b) {
    return String(a).replace(/-/g, '').toLowerCase() === String(b).replace(/-/g, '').toLowerCase();
}

function populateLinkForm(view) {
    // Provider dropdown
    var provSel = view.querySelector('#LinkProviderId');
    provSel.innerHTML = (_config.Providers || []).map(function (p) {
        return '<option value="' + escAttr(p.Id) + '">' + escHtml(p.Name || p.Id) + '</option>';
    }).join('');

    // Jellyfin user dropdown — only unlinked users (linked ones can't be re-linked anyway)
    var userSel = view.querySelector('#LinkUserId');
    userSel.innerHTML = _users.map(function (u) {
        return '<option value="' + escAttr(u.Id) + '"' + (u.Linked ? ' disabled' : '') + '>'
            + escHtml(u.Name) + (u.Linked ? ' (already linked)' : '') + '</option>';
    }).join('');
}

// ─── Save ────────────────────────────────────────────────────────────────────

function save(view) {
    var providers = collectProviders(view);
    var cfg = {
        Providers: providers,
        GroupMappings: collectMappings(view),
        Links: (_config && _config.Links) || [],
        SeenGroups: (_config && _config.SeenGroups) || [],
        PreserveAdminPermissions: getChk(view, 'PreserveAdminPermissions'),
        SignOutOfIdentityProvider: getChk(view, 'SignOutOfIdentityProvider'),
        AddLoginButtonToBranding: getChk(view, 'AddLoginButtonToBranding'),
        UseProviderLogos: getChk(view, 'UseProviderLogos'),
        InjectLoginButtonIntoForm: getChk(view, 'InjectLoginButtonIntoForm'),
        AllowSelfServiceLinking: getChk(view, 'AllowSelfServiceLinking'),
        SyncExcludedUserIds: collectExcluded(view),
    };
    apiPost(API + '/config', cfg).then(function () {
        _config = cfg;
        renderProviders(view, providers);
        return populateUsers(view);
    }).then(function () {
        Dashboard.processPluginConfigurationUpdateResult();
    }).catch(function (e) { Dashboard.alert('Save failed: ' + e.message); });
}

// ─── Providers ────────────────────────────────────────────────────────────────

function renderProviders(view, providers) {
    var c = view.querySelector('#Providers');
    c.innerHTML = '';
    providers.forEach(function (p, i) { c.appendChild(buildProviderCard(p, i)); });
    retitleProviders(view);
}

function buildProviderCard(p, i) {
    // A card per provider, foldable: with three of them the page is otherwise a wall of
    // fields, and the one being edited is the only one worth seeing.
    var card = document.createElement('details');
    card.className = 'sso-provider sso-card';
    card.open = true;
    card.dataset.providerId = p.Id || newId();

    var loginUrl = '/sso/oidc/start/' + card.dataset.providerId;
    var redirectUri = window.location.origin + '/sso/oidc/callback/' + card.dataset.providerId;
    var logoutUri = window.location.origin + '/sso/oidc/backchannel-logout/' + card.dataset.providerId;
    var scopes = (p.Scopes || []).join(' ');

    card.innerHTML =
        '<summary class="sso-card-summary">' +
            '<span class="sso-provider-title sso-card-name">' +
                escHtml(p.Name || ('Provider ' + (i + 1))) + '</span>' +
            '<span class="sso-card-sub">' +
                escHtml(p.Authority || 'no issuer yet') + '</span>' +
        '</summary>' +
        '<div class="sso-card-body">' +
        '<div class="sso-mapping-head">' +
            '<div class="inputContainer sso-group-field">' +
                '<input type="text" is="emby-input" class="sso-provider-name" label="Display name" ' +
                    'value="' + escAttr(p.Name || '') + '" />' +
            '</div>' +
            '<div class="checkboxContainer sso-admin-toggle"><label>' +
                '<input type="checkbox" is="emby-checkbox" class="sso-provider-enabled"' +
                (p.Enabled ? ' checked' : '') + ' /><span>Enabled</span></label></div>' +
            '<button type="button" is="emby-button" class="raised sso-provider-remove">' +
                '<span>Remove</span></button>' +
        '</div>' +
        '<div class="inputContainer">' +
            '<input type="url" is="emby-input" class="sso-provider-authority" ' +
                'label="Issuer URL (Authority)" placeholder="https://auth.example.com/application/o/jellyfin" ' +
                'value="' + escAttr(p.Authority || '') + '" />' +
            '<div class="fieldDescription">Must serve <code>/.well-known/openid-configuration</code>.</div>' +
        '</div>' +
        '<div class="inputContainer">' +
            '<input type="text" is="emby-input" class="sso-provider-client" label="Client ID" ' +
                'value="' + escAttr(p.ClientId || '') + '" />' +
        '</div>' +
        '<div class="inputContainer">' +
            '<input type="text" is="emby-input" class="sso-provider-groups-claim" ' +
                'label="Groups claim name" placeholder="groups" ' +
                'value="' + escAttr(p.GroupsClaim || '') + '" />' +
        '</div>' +
        '<div class="checkboxContainer"><label>' +
            '<input type="checkbox" is="emby-checkbox" class="sso-provider-userinfo"' +
            (p.GroupsFromUserInfo ? ' checked' : '') +
            ' /><span>Fetch groups from userinfo endpoint instead of ID token</span></label></div>' +
        '<div class="checkboxContainer">' +
            '<label><input type="checkbox" is="emby-checkbox" class="sso-provider-backchannel"' +
            (p.AcceptBackChannelLogout === false ? '' : ' checked') +
            ' /><span>Accept back-channel logout from this provider</span></label>' +
            '<div class="fieldDescription">Ends the Jellyfin session when the provider ' +
                'says the session is over, instead of waiting for the next login. Register ' +
                'the back-channel logout URL below with the provider; Authelia has no such ' +
                'notification to send.</div>' +
        '</div>' +
        '<div class="checkboxContainer">' +
            '<label><input type="checkbox" is="emby-checkbox" class="sso-provider-branded"' +
            (p.BrandedLoginButton ? ' checked' : '') +
            ' /><span>Paint the login button in this provider\'s colour</span></label>' +
            '<div class="fieldDescription">Off, the button looks like Jellyfin\'s other ' +
                'buttons with the provider\'s colour on its left edge. On, the whole button ' +
                'carries that colour.</div>' +
        '</div>' +
        '<div class="inputContainer sso-colour-field"' +
            (p.BrandedLoginButton ? '' : ' hidden') + '>' +
            '<input type="text" is="emby-input" class="sso-provider-colour" ' +
                'label="Button colour" placeholder="#FD4B2D" ' +
                'value="' + escAttr(p.ButtonColour || '') + '" />' +
            '<div class="fieldDescription">Empty uses the colour the provider is ' +
                'recognised by. <code>#rgb</code> or <code>#rrggbb</code>; anything else ' +
                'is ignored.</div>' +
        '</div>' +
        '<div class="inputContainer">' +
            '<input type="text" is="emby-input" class="sso-provider-scopes" ' +
                'label="Scopes (space separated, empty = openid profile email)" ' +
                'value="' + escAttr(scopes) + '" />' +
            '<div class="fieldDescription">Authelia needs <code>groups</code> added here; ' +
                'Keycloak rejects it. Leave empty unless your provider says otherwise.</div>' +
        '</div>' +
        '<div class="sso-copy-list">' +
            copyRow('Redirect URI for your provider', redirectUri) +
            copyRow('Login URL', window.location.origin + loginUrl) +
            copyRow('Back-channel logout URL', logoutUri) +
            copyRow('Provider ID', card.dataset.providerId) +
        '</div>' +
        '</div>';

    card.querySelector('.sso-provider-remove').addEventListener('click', function () {
        card.remove();
    });

    card.querySelector('.sso-provider-name').addEventListener('input', function () {
        retitleProviders(card.closest('.sso-panel') || document);
    });

    // The colour only applies to a painted button, so it only appears with one.
    var authority = card.querySelector('.sso-provider-authority');
    authority.addEventListener('input', function () {
        card.querySelector('.sso-card-sub').textContent = authority.value.trim() || 'no issuer yet';
    });

    var branded = card.querySelector('.sso-provider-branded');
    branded.addEventListener('change', function () {
        card.querySelector('.sso-colour-field').hidden = !branded.checked;
    });

    card.querySelectorAll('.sso-copy').forEach(function (button) {
        button.addEventListener('click', function () {
            copyText(button.dataset.copy).then(function () {
                var span = button.querySelector('span');
                var previous = span.textContent;
                span.textContent = 'Copied';
                setTimeout(function () { span.textContent = previous; }, 1500);
            });
        });
    });

    return card;
}

/// Each card is titled by what the administrator typed, so several providers can be told
/// apart at a glance; a card with no name yet falls back to its position.
function retitleProviders(view) {
    Array.from(view.querySelectorAll('.sso-provider')).forEach(function (card, idx) {
        var title = card.querySelector('.sso-provider-title');
        var name = card.querySelector('.sso-provider-name');
        if (title) {
            title.textContent = (name && name.value.trim()) || ('Provider ' + (idx + 1));
        }
    });
}

function collectProviders(view) {
    return Array.from(view.querySelectorAll('.sso-provider')).map(function (card) {
        var val = function (cls) {
            var el = card.querySelector(cls);
            return el ? el.value.trim() : '';
        };
        var authority = val('.sso-provider-authority');
        var clientId = val('.sso-provider-client');
        var name = val('.sso-provider-name');
        // A provider saved with only a name is kept on purpose: its id is what the
        // identity provider's redirect URI has to contain, so it has to exist before
        // the rest can be filled in.
        if (!authority && !clientId && !name) return null;

        var previous = ((_config && _config.Providers) || []).filter(function (p) {
            return p.Id === card.dataset.providerId;
        })[0] || {};

        return {
            Id: card.dataset.providerId,
            Name: name || 'SSO',
            Enabled: card.querySelector('.sso-provider-enabled').checked,
            Authority: authority,
            ClientId: clientId,
            ClientType: previous.ClientType || 0,
            ConfidentialSecretRef: previous.ConfidentialSecretRef || null,
            Scopes: val('.sso-provider-scopes').split(/\s+/).filter(Boolean),
            GroupsClaim: val('.sso-provider-groups-claim') || 'groups',
            GroupsFromUserInfo: card.querySelector('.sso-provider-userinfo').checked,
            BrandedLoginButton: card.querySelector('.sso-provider-branded').checked,
            AcceptBackChannelLogout: card.querySelector('.sso-provider-backchannel').checked,
            ButtonColour: val('.sso-provider-colour'),
        };
    }).filter(Boolean);
}

// ─── Mapping table ────────────────────────────────────────────────────────────

function renderMappings(view, mappings) {
    var c = view.querySelector('#GroupMappings');
    c.innerHTML = '';
    mappings.forEach(function (m, i) { c.appendChild(buildRow(view, m, i)); });
}

function buildRow(view, m, i) {
    var card = document.createElement('details');
    card.className = 'sso-mapping sso-card';
    card.open = true;

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
    var rating = (m.MaxParentalRating === null || m.MaxParentalRating === undefined) ? '' : m.MaxParentalRating;

    function permission(cls, labelText, on) {
        return '<div class="sectioncheckbox"><label><input type="checkbox" is="emby-checkbox" class="' + cls + '"'
            + (on ? ' checked' : '') + ' /><span>' + labelText + '</span></label></div>';
    }

    // Built as innerHTML so Jellyfin's emby-* custom elements auto-upgrade on DOM insertion.
    card.innerHTML =
        '<summary class="sso-card-summary">' +
            '<span class="sso-mapping-title sso-card-name">' +
                escHtml(m.Group || ('Group ' + (i + 1))) + '</span>' +
            '<span class="sso-card-sub">' +
                (m.IsAdministrator ? 'administrator' : ((m.LibraryIds || []).length + ' librar'
                    + (((m.LibraryIds || []).length === 1) ? 'y' : 'ies'))) + '</span>' +
        '</summary>' +
        '<div class="sso-card-body">' +
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
        '</div>' +
        '<div class="sso-perms-section">' +
            '<h3 class="checkboxListLabel">Permissions</h3>' +
            '<div class="checkboxList sso-libs-grid">' +
                permission('sso-livetv-cb', 'Live TV access', m.EnableLiveTvAccess) +
                permission('sso-livetv-mgmt-cb', 'Live TV management', m.EnableLiveTvManagement) +
                permission('sso-download-cb', 'Downloads', m.EnableContentDownloading) +
            '</div>' +
            '<div class="inputContainer sso-rating-field">' +
                '<input type="number" is="emby-input" class="sso-rating-input" min="0" ' +
                    'label="Max parental rating (empty = no limit)" value="' + escAttr(String(rating)) + '" />' +
            '</div>' +
        '</div>' +
        '</div>';

    var ac = card.querySelector('.sso-admin-check');
    var libsSection = card.querySelector('.sso-libs-section');
    var permsSection = card.querySelector('.sso-perms-section');

    // Admins get everything, so the per-group grants are irrelevant while admin is checked.
    function syncAdmin() {
        libsSection.style.display = ac.checked ? 'none' : '';
        permsSection.style.display = ac.checked ? 'none' : '';
    }
    ac.addEventListener('change', syncAdmin);
    syncAdmin();

    card.querySelector('.sso-remove-btn').addEventListener('click', function () {
        card.remove();
        renumber(view);
    });

    var group = card.querySelector('.sso-group-input');
    group.addEventListener('input', function () {
        card.querySelector('.sso-card-name').textContent =
            group.value.trim() || ('Group ' + (i + 1));
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
        var ratingInput = card.querySelector('.sso-rating-input');
        var rating = ratingInput ? ratingInput.value.trim() : '';
        var checked = function (cls) {
            var cb = card.querySelector(cls);
            return cb ? cb.checked : false;
        };

        return {
            Group: g,
            LibraryIds: libIds,
            IsAdministrator: ac ? ac.checked : false,
            EnableLiveTvAccess: checked('.sso-livetv-cb'),
            EnableLiveTvManagement: checked('.sso-livetv-mgmt-cb'),
            EnableContentDownloading: checked('.sso-download-cb'),
            MaxParentalRating: rating === '' ? null : parseInt(rating, 10),
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
/// One printed value with a button that puts it on the clipboard.
/// One row of the address grid: label, value, button — three cells of the same grid, so
/// the columns line up however long the values are.
function copyRow(label, value) {
    return '<span class="sso-copy-label">' + escHtml(label) + '</span>' +
        '<code>' + escHtml(value) + '</code>' +
        '<button type="button" is="emby-button" class="raised sso-copy" ' +
            'data-copy="' + escAttr(value) + '"><span>Copy</span></button>';
}

/// navigator.clipboard needs a secure context, which a LAN server over plain HTTP is
/// not, so fall back to the old selection trick there.
function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
        return navigator.clipboard.writeText(text);
    }

    var area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    try { document.execCommand('copy'); } finally { document.body.removeChild(area); }
    return Promise.resolve();
}

function escHtml(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
function escAttr(s) { return escHtml(s).replace(/"/g, '&quot;').replace(/'/g, '&#39;'); }

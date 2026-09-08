const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const html = fs.readFileSync(path.join(__dirname, '../Jellyfin.Plugin.Lastfm/Configuration/configPage.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];

function harness(initial) {
    const state = { configuration: structuredClone(initial), updates: [], logins: [], alerts: [], loading: false };
    class Element {
        constructor() { this.value = ''; this.checked = false; this.disabled = false; this.children = []; this.events = {}; }
        addEventListener(name, callback) { (this.events[name] ||= []).push(callback); }
        async emit(name) { for (const callback of this.events[name] || []) await callback({ preventDefault() {} }); }
        replaceChildren() { this.children = []; this.value = ''; }
        appendChild(child) { this.children.push(child); if (!this.value) this.value = child.value; }
    }
    const ids = ['lastfmUser', 'lastfmUsername', 'lastfmPassword', 'lastfmScrobble', 'lastfmFavourites', 'lastfmAlternative', 'lastfmAccountStatus'];
    const elements = Object.fromEntries(ids.map(id => [id, new Element()]));
    const form = new Element();
    form.querySelectorAll = () => Object.values(elements);
    const page = new Element();
    page.querySelector = selector => selector === 'form' ? form : elements[selector.slice(1)];
    const api = {
        getPluginConfiguration: async () => structuredClone(state.configuration),
        getUsers: async () => [{ Id: 'user-1', Name: '<img src=x onerror=alert(1)>' }, { Id: 'user-2', Name: 'Second user' }],
        getUrl: route => '/jellyfin/' + route,
        ajax: async request => {
            state.logins.push(request);
            return { session: { name: 'canonical-name', key: 'new-session-fixture' } };
        },
        updatePluginConfiguration: async (_, configuration) => {
            state.updates.push(structuredClone(configuration));
            state.configuration = structuredClone(configuration);
            return {};
        }
    };
    vm.runInNewContext(script, {
        document: { getElementById: () => page, createElement: () => new Element() },
        ApiClient: api,
        Dashboard: {
            showLoadingMsg: () => { state.loading = true; },
            hideLoadingMsg: () => { state.loading = false; },
            alert: message => state.alerts.push(message),
            processPluginConfigurationUpdateResult() {}
        }
    });
    return { state, elements, form, page, api };
}

function configuration() {
    return { LastfmUsers: [{ MediaBrowserUserId: 'user-1', Username: 'existing', SessionKey: 'saved-session-fixture', Options: { Scrobble: true, SyncFavourites: false, AlternativeMode: false } }] };
}

test('opening settings never puts a saved session key into the password field', async () => {
    const h = harness(configuration());
    await h.page.emit('pageshow');
    assert.equal(h.elements.lastfmPassword.value, '');
    assert.equal(h.elements.lastfmUsername.value, 'existing');
    assert.equal(h.elements.lastfmUser.children[0].textContent, '<img src=x onerror=alert(1)>');
    assert.equal(h.elements.lastfmUser.children[0].innerHTML, undefined);
    assert.match(html, /id="lastfmPassword" type="password"/);
});

test('option-only changes preserve the saved account and concurrent changes to other users', async () => {
    const h = harness(configuration());
    await h.page.emit('pageshow');
    h.state.configuration.LastfmUsers.push({ MediaBrowserUserId: 'user-2', Username: 'other', SessionKey: 'other-session-fixture' });
    h.elements.lastfmFavourites.checked = true;
    await h.form.emit('submit');
    assert.equal(h.state.logins.length, 0);
    assert.equal(h.state.updates[0].LastfmUsers[0].SessionKey, 'saved-session-fixture');
    assert.equal(h.state.updates[0].LastfmUsers[0].Options.SyncFavourites, true);
    assert.equal(h.state.updates[0].LastfmUsers[1].Username, 'other');
    assert.equal(h.state.loading, false);
});

test('connecting an account exchanges a password once and saves only the returned session', async () => {
    const h = harness({ LastfmUsers: [] });
    await h.page.emit('pageshow');
    h.elements.lastfmUsername.value = 'new-name';
    h.elements.lastfmPassword.value = 'test-password';
    h.elements.lastfmScrobble.checked = true;
    await h.form.emit('submit');
    assert.equal(h.state.logins.length, 1);
    assert.equal(h.state.logins[0].url, '/jellyfin/Lastfm/Login');
    assert.deepEqual(JSON.parse(h.state.logins[0].data), { username: 'new-name', password: 'test-password' });
    assert.equal(h.state.updates[0].LastfmUsers[0].Username, 'canonical-name');
    assert.equal(h.state.updates[0].LastfmUsers[0].SessionKey, 'new-session-fixture');
    assert.equal(JSON.stringify(h.state.updates).includes('test-password'), false);
    assert.equal(h.elements.lastfmPassword.value, '');
});

test('changing a connected username requires a new password', async () => {
    const h = harness(configuration());
    await h.page.emit('pageshow');
    h.elements.lastfmUsername.value = 'different-account';
    await h.form.emit('submit');
    assert.equal(h.state.updates.length, 0);
    assert.equal(h.state.alerts.length, 1);
    assert.equal(h.elements.lastfmUser.disabled, false);
});

test('login failures retain existing configuration and release loading state', async () => {
    const h = harness(configuration());
    await h.page.emit('pageshow');
    h.elements.lastfmPassword.value = 'test-password';
    h.api.ajax = async () => { throw new Error('sensitive-upstream-fixture'); };
    await h.form.emit('submit');
    assert.equal(h.state.updates.length, 0);
    assert.equal(h.state.loading, false);
    assert.equal(h.elements.lastfmPassword.value, '');
    assert.equal(h.state.alerts.join('').includes('sensitive-upstream-fixture'), false);
});

test('reopening the page does not register duplicate submit handlers', async () => {
    const h = harness(configuration());
    await h.page.emit('pageshow');
    await h.page.emit('pageshow');
    await h.form.emit('submit');
    assert.equal(h.state.updates.length, 1);
    h.elements.lastfmPassword.value = 'test-password';
    await h.page.emit('pagehide');
    assert.equal(h.elements.lastfmPassword.value, '');
});

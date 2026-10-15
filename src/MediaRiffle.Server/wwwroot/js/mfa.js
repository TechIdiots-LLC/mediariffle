// Two-step verification: passkeys (Face ID, Touch ID, Windows Hello, a phone or a security key) and authenticator
// apps' 6-digit codes, with one-time recovery codes. Admin asks for one; an admin without one is walked through
// setting it up there, and manages them under Profile.

import { api } from './api.js';
import { h, mount } from './dom.js';

// ── Passkeys in the browser ──────────────────────────────────────────────────

/** Passkeys need a secure page (HTTPS, or localhost) and a browser with WebAuthn. */
export const passkeysSupported = () => !!window.PublicKeyCredential && window.isSecureContext;

const fromB64url = s => {
  const b = s.replace(/-/g, '+').replace(/_/g, '/');
  return Uint8Array.from(atob(b + '='.repeat((4 - b.length % 4) % 4)), c => c.charCodeAt(0)).buffer;
};
const toB64url = buf => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

// The server's options are WebAuthn's JSON form; browsers without parse…FromJSON (older Safari, Firefox) get the
// byte fields turned into buffers by hand
function creationOptions(json) {
  if (PublicKeyCredential.parseCreationOptionsFromJSON) return PublicKeyCredential.parseCreationOptionsFromJSON(json);
  return {
    ...json, challenge: fromB64url(json.challenge), user: { ...json.user, id: fromB64url(json.user.id) },
    excludeCredentials: (json.excludeCredentials ?? []).map(c => ({ ...c, id: fromB64url(c.id) })),
  };
}

function requestOptions(json) {
  if (PublicKeyCredential.parseRequestOptionsFromJSON) return PublicKeyCredential.parseRequestOptionsFromJSON(json);
  return { ...json, challenge: fromB64url(json.challenge), allowCredentials: (json.allowCredentials ?? []).map(c => ({ ...c, id: fromB64url(c.id) })) };
}

// The browser's answer as the server reads it (toJSON where the browser has it)
function credentialJson(c) {
  if (typeof c.toJSON === 'function') return JSON.stringify(c);
  const r = c.response;
  const response = r.attestationObject
    ? { clientDataJSON: toB64url(r.clientDataJSON), attestationObject: toB64url(r.attestationObject), transports: r.getTransports?.() ?? [] }
    : {
      clientDataJSON: toB64url(r.clientDataJSON), authenticatorData: toB64url(r.authenticatorData),
      signature: toB64url(r.signature), userHandle: r.userHandle ? toB64url(r.userHandle) : null,
    };
  return JSON.stringify({
    id: c.id, rawId: toB64url(c.rawId), type: c.type, authenticatorAttachment: c.authenticatorAttachment ?? null,
    response, clientExtensionResults: c.getClientExtensionResults?.() ?? {},
  });
}

// Cancelling the browser's passkey sheet isn't an error worth shouting about
const cancelled = e => e?.name === 'NotAllowedError' || e?.name === 'AbortError';

async function createPasskey(name) {
  const options = await api('/api/auth/mfa/passkeys/options', { method: 'POST' });
  const credential = await navigator.credentials.create({ publicKey: creationOptions(options) });
  return api('/api/auth/mfa/passkeys', { method: 'POST', body: { credential: credentialJson(credential), name } });
}

async function verifyWithPasskey() {
  const options = await api('/api/auth/mfa/verify/passkey/options', { method: 'POST' });
  const credential = await navigator.credentials.get({ publicKey: requestOptions(options) });
  return api('/api/auth/mfa/verify/passkey', { method: 'POST', body: { credential: credentialJson(credential) } });
}

/** A name for a new passkey, from the device it's made on. */
function deviceName() {
  const ua = navigator.userAgent;
  const device = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Macintosh/.test(ua) ? (navigator.maxTouchPoints > 1 ? 'iPad' : 'Mac')
    : /Android/.test(ua) ? 'Android' : /Windows/.test(ua) ? 'Windows' : /CrOS/.test(ua) ? 'Chromebook' : /Linux/.test(ua) ? 'Linux' : '';
  return device ? `${device} passkey` : 'Passkey';
}

const status = () => api('/api/auth/mfa');

// ── Confirming it's you ──────────────────────────────────────────────────────

/** A passkey button and/or a code box; `onDone` once the server accepts one. */
function verifyBox(s, onDone) {
  const msg = h('p', { class: 'error' });
  const fail = e => { msg.textContent = cancelled(e) ? 'The passkey was cancelled; try again.' : e.message; };
  const code = h('input', { inputmode: 'numeric', autocomplete: 'one-time-code', placeholder: s.authenticator ? '123456' : 'Recovery code', size: 16, required: true });
  const usePasskey = s.passkeys.length > 0 && passkeysSupported();
  const passkeyBtn = usePasskey ? h('button', { type: 'button', onclick: async () => {
    msg.textContent = '';
    try { await verifyWithPasskey(); onDone(); } catch (e) { fail(e); }
  } }, '🔑 Use a passkey') : null;
  const codeForm = h('form', { class: 'row', onsubmit: async e => {
    e.preventDefault();
    msg.textContent = '';
    try {
      const r = await api('/api/auth/mfa/verify/code', { method: 'POST', body: { code: code.value } });
      if (r?.recoveryCodesLeft != null) alert(`Recovery code used. ${r.recoveryCodesLeft} left; make new ones under Profile if that's running low.`);
      onDone();
    } catch (err) { fail(err); }
  } },
    code, h('button', { type: 'submit', class: usePasskey ? 'secondary' : '' }, 'Verify'));
  const box = h('div', { class: 'mfa-verify' },
    passkeyBtn,
    h('p', { class: 'muted' }, s.authenticator
      ? (usePasskey ? 'Or enter the 6-digit code from your authenticator app (or a recovery code):' : 'Enter the 6-digit code from your authenticator app, or a recovery code:')
      : (usePasskey ? 'Lost the passkey? Enter a recovery code:' : 'Enter a recovery code:')),
    codeForm, msg);
  // Safari opens the passkey sheet only from a click, so it waits for one
  setTimeout(() => (passkeyBtn ?? code).focus(), 0);
  return box;
}

// ── Setting one up ───────────────────────────────────────────────────────────

/**
 * The guided setup: the password again (nothing's set up yet, so that's what proves it's you), a passkey or an
 * authenticator app, then the recovery codes to keep. `onDone` at the end. `authenticatorOnly` goes straight to the
 * authenticator part (Profile, adding one alongside passkeys).
 */
function setupWizard(box, s, onDone, intro = null, authenticatorOnly = false) {
  const step = (...children) => mount(box, h('div', { class: 'card mfa-setup' }, intro, children));

  const password = () => {
    const pass = h('input', { type: 'password', autocomplete: 'current-password', required: true });
    const msg = h('p', { class: 'error' });
    step(h('h2', {}, 'Step 1 of 3 · Confirm your password'),
      h('form', { class: 'row', onsubmit: async e => {
        e.preventDefault();
        try { await api('/api/auth/mfa/password', { method: 'POST', body: { password: pass.value } }); choose(); }
        catch (err) { msg.textContent = err.message; }
      } }, pass, h('button', { type: 'submit' }, 'Continue')),
      msg);
    pass.focus();
  };

  const choose = () => {
    const name = h('input', { value: deviceName(), maxlength: 60, size: 22 });
    const msg = h('p', { class: 'error' });
    const ok = passkeysSupported();
    step(h('h2', {}, 'Step 2 of 3 · Choose how to verify'),
      h('div', { class: 'mfa-choices' },
        h('div', { class: 'card' },
          h('h3', {}, '🔑 A passkey', h('span', { class: 'muted' }, ' · recommended')),
          h('p', { class: 'muted' }, 'Face ID or Touch ID (iPhone, iPad, Mac), Windows Hello, an Android phone, or a security key. ' +
            'Nothing to type, and nothing that can be phished. On Apple devices it\'s kept in iCloud Keychain, so it\'s on all of them.'),
          ok ? [h('label', {}, 'Name ', name),
            h('button', { onclick: async () => {
              msg.textContent = '';
              try { const r = await createPasskey(name.value); codes(r.recoveryCodes); }
              catch (e) { msg.textContent = cancelled(e) ? 'The passkey was cancelled; try again.' : e.message; }
            } }, 'Create a passkey')]
            : h('p', {}, 'This browser can\'t make one here (passkeys need the HTTPS address and a recent browser).')),
        h('div', { class: 'card' },
          h('h3', {}, '🔢 An authenticator app'),
          h('p', { class: 'muted' }, '6-digit codes from Apple\'s Passwords app (built into iPhone, iPad and Mac), Google Authenticator, ' +
            'Microsoft Authenticator, 1Password, Bitwarden…'),
          h('button', { class: ok ? 'secondary' : '', onclick: authenticator }, 'Set up an authenticator app'))),
      h('p', { class: 'muted' }, 'You can add the other one too, later, under Profile.'),
      msg);
  };

  const authenticator = async () => {
    let a;
    try { a = await api('/api/auth/mfa/authenticator/start', { method: 'POST' }); }
    catch (e) { mount(box, h('p', { class: 'error' }, e.message)); return; }
    const code = h('input', { inputmode: 'numeric', autocomplete: 'one-time-code', placeholder: '123456', size: 10, required: true });
    const msg = h('p', { class: 'error' });
    step(h('h2', {}, 'Step 2 of 3 · Add MediaRiffle to your authenticator app'),
      h('div', { class: 'mfa-totp' },
        h('img', { class: 'mfa-qr', src: a.qr, alt: 'QR code for the authenticator app' }),
        h('div', {},
          h('p', {}, 'Scan the QR code with your phone\'s camera or authenticator app.'),
          h('p', { class: 'muted' }, 'On this phone or Mac already? ', h('a', { href: a.uri }, 'Open it in the authenticator app'),
            ' (Apple\'s Passwords app picks it up).'),
          h('p', { class: 'muted' }, 'Or type this key in: ', h('code', { class: 'mfa-key' }, a.key)),
          h('form', { class: 'row', onsubmit: async e => {
            e.preventDefault();
            msg.textContent = '';
            try { const r = await api('/api/auth/mfa/authenticator/enable', { method: 'POST', body: { code: code.value } }); codes(r.recoveryCodes); }
            catch (err) { msg.textContent = err.message; }
          } }, h('label', {}, 'Then enter the code it shows ', code), h('button', { type: 'submit' }, 'Verify')),
          msg)));
    code.focus();
  };

  // Recovery codes are made with the first passkey or authenticator; adding another doesn't make new ones
  const codes = list => {
    if (!list?.length) { onDone(); return; }
    step(h('h2', {}, 'Step 3 of 3 · Save your recovery codes'), recoveryCodes(list, onDone));
  };

  if (authenticatorOnly) authenticator();
  else if (s.verified || s.passwordConfirmed) choose();
  else password();
}

/** The recovery codes, to copy or download, and a Continue that waits until they're saved. */
function recoveryCodes(list, onDone, doneLabel = 'Continue') {
  const text = `MediaRiffle recovery codes (${location.host}) — each works once:\n\n${list.join('\n')}\n`;
  const saved = h('input', { type: 'checkbox' });
  const go = h('button', { disabled: true, onclick: onDone }, doneLabel);
  saved.addEventListener('change', () => { go.disabled = !saved.checked; });
  return [
    h('p', {}, 'If you lose your passkey or phone, each of these lets you in once. Keep them somewhere safe, like a password ' +
      'manager. They won\'t be shown again.'),
    h('ul', { class: 'mfa-codes' }, list.map(c => h('li', {}, h('code', {}, c)))),
    h('div', { class: 'row' },
      h('button', { class: 'secondary', onclick: async e => {
        try { await navigator.clipboard.writeText(text); e.target.textContent = 'Copied ✓'; } catch { alert(text); }
      } }, 'Copy'),
      h('button', { class: 'secondary', onclick: () => {
        const a = h('a', { href: URL.createObjectURL(new Blob([text], { type: 'text/plain' })), download: 'mediariffle-recovery-codes.txt' });
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 1000);
      } }, 'Download')),
    h('label', { class: 'row' }, saved, 'I\'ve saved these somewhere safe'),
    go,
  ];
}

// ── Admin's door ─────────────────────────────────────────────────────────────

/**
 * In place of Admin until this session has verified: a check, or (nothing set up yet) the guided setup. `onDone` opens
 * Admin.
 */
export async function adminGate(view, onDone) {
  const s = await status();
  if (!s.required || s.verified) { onDone(); return; }
  const box = h('div');
  mount(view, h('h1', {}, 'Admin'), box);
  if (s.setUp) {
    mount(box, h('div', { class: 'card mfa-gate' },
      h('h2', {}, 'Confirm it\'s you'),
      h('p', { class: 'muted' }, 'Admin asks for your passkey or authenticator code every so often.'),
      verifyBox(s, onDone)));
  } else {
    setupWizard(box, s, onDone, [
      h('h2', {}, 'Admin needs two-step verification'),
      h('p', {}, 'Admins can add and remove libraries, users and invites, so Admin asks for a second step besides your ' +
        'password: a passkey, or a code from an authenticator app. It takes a minute to set up, once.')]);
  }
}

// ── Profile ──────────────────────────────────────────────────────────────────

/** Profile → Two-step verification: passkeys, the authenticator app and recovery codes. */
export async function mfaSettings(box) {
  let s;
  try { s = await status(); } catch (e) { mount(box, h('p', { class: 'error' }, e.message)); return; }
  const redraw = () => mfaSettings(box);
  const msg = h('p', { class: 'error' });
  const check = h('div');

  // Changing things wants a recent check: ask for one, then do it. requireCheck asks before starting something that
  // takes several steps, rather than partway through
  const requireCheck = async () => {
    if (!(await status()).verified) throw Object.assign(new Error('Confirm it\'s you first.'), { data: { mfa: 'verify' } });
  };
  const withCheck = fn => async () => {
    msg.textContent = '';
    try { await fn(); }
    catch (e) {
      if (e.data?.mfa === 'verify') {
        mount(check, h('div', { class: 'card' }, h('h3', {}, 'Confirm it\'s you first'),
          verifyBox(await status(), async () => { mount(check); try { await fn(); } catch (err) { msg.textContent = err.message; } })));
      } else if (!cancelled(e)) msg.textContent = e.message;
    }
  };

  if (!s.setUp) {
    mount(box,
      h('p', { class: 'muted' }, 'Not set up. A passkey or an authenticator app adds a second step besides your password' +
        (s.required ? '; Admin asks for it.' : '.')),
      h('button', { onclick: () => setupWizard(box, s, redraw) }, 'Set up two-step verification'));
    return;
  }

  const name = h('input', { value: deviceName(), maxlength: 60, size: 20 });
  mount(box,
    h('h3', {}, 'Passkeys'),
    s.passkeys.length
      ? h('table', {}, s.passkeys.map(p => h('tr', {},
          h('td', {}, '🔑 ', p.name, p.synced ? h('span', { class: 'muted' }, ' · synced') : null),
          h('td', { class: 'muted' }, `Added ${new Date(p.created).toLocaleDateString()}`),
          h('td', {}, h('button', { class: 'secondary', onclick: withCheck(async () => {
            if (!confirm(`Remove the passkey "${p.name}"?`)) return;
            await api(`/api/auth/mfa/passkeys/${p.id}`, { method: 'DELETE' });
            redraw();
          }) }, 'Remove')))))
      : h('p', { class: 'muted' }, 'None.'),
    passkeysSupported()
      ? h('div', { class: 'row' }, name, h('button', { class: 'secondary', onclick: withCheck(async () => {
          await requireCheck();
          try { await createPasskey(name.value); redraw(); }
          catch (e) { if (cancelled(e)) msg.textContent = 'The passkey was cancelled.'; else throw e; }
        }) }, 'Add a passkey'))
      : h('p', { class: 'muted' }, 'Passkeys can be added from a browser on the HTTPS address.'),

    h('h3', {}, 'Authenticator app'),
    s.authenticator
      ? h('div', { class: 'row' }, h('span', {}, '🔢 On'), h('button', { class: 'secondary', onclick: withCheck(async () => {
          if (!confirm('Turn off the authenticator app? Its codes will stop working here.')) return;
          await api('/api/auth/mfa/authenticator', { method: 'DELETE' });
          redraw();
        }) }, 'Turn off'))
      : h('div', { class: 'row' }, h('span', { class: 'muted' }, 'Off'), h('button', { class: 'secondary', onclick: withCheck(async () => {
          await requireCheck();
          setupWizard(box, s, redraw, null, true);
        }) }, 'Set up')),

    h('h3', {}, 'Recovery codes'),
    h('div', { class: 'row' },
      h('span', { class: s.recoveryCodesLeft < 3 ? 'error' : 'muted' }, `${s.recoveryCodesLeft} left`),
      h('button', { class: 'secondary', onclick: withCheck(async () => {
        const r = await api('/api/auth/mfa/recovery-codes', { method: 'POST' });
        mount(box, h('h3', {}, 'New recovery codes'), recoveryCodes(r.recoveryCodes, redraw, 'Done'));
      }) }, 'Make new codes')),
    check, msg);
}

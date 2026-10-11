import { h, loginScreen, phoneInput } from '../dom.js';
import { login } from '../api.js';

const CLUB_KEY = 'clubos.admin.club';

function lastClub() {
  try { return window.localStorage.getItem(CLUB_KEY) || ''; } catch { return ''; }
}

function rememberClub(value) {
  try { window.localStorage.setItem(CLUB_KEY, value); } catch { /* не страшно */ }
}

/** Экран входа. onDone вызывается после успешного входа. */
export function renderLogin(root, { onDone, message = '' }) {
  const club = h('input', { name: 'club', autocomplete: 'organization', placeholder: 'например, demo', value: lastClub(), required: true, maxLength: 40 });
  const phone = phoneInput({ name: 'phone', autocomplete: 'username', required: true });
  const password = h('input', { name: 'password', type: 'password', autocomplete: 'current-password', required: true, maxLength: 128 });
  const error = h('div', { class: 'error-text', role: 'alert' }, message);
  const submit = h('button', { class: 'btn btn-primary', type: 'submit' }, 'Войти');

  const form = h('form', {
    onsubmit: async (event) => {
      event.preventDefault();
      error.textContent = '';
      submit.disabled = true;
      try {
        await login(club.value.trim().toLowerCase(), phone.value.trim(), password.value);
        rememberClub(club.value.trim().toLowerCase());
        password.value = '';
        onDone();
      } catch (e) {
        error.textContent = e.message;
        password.select();
      } finally {
        submit.disabled = false;
      }
    },
  },
    h('label', { class: 'field' }, h('span', {}, 'Код клуба'), club),
    h('label', { class: 'field' }, h('span', {}, 'Телефон сотрудника'), phone),
    h('label', { class: 'field' }, h('span', {}, 'Пароль'), password),
    error, submit);

  loginScreen(root, { brand: 'ClubOS, админка клуба', title: 'Вход', intro: 'Код клуба вам выдали при подключении к ClubOS.', form });
  (club.value ? phone : club).focus();
}

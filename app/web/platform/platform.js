// Панель владельца платформы: рекламные баннеры партнёров для экранов блокировки, клубы, отчёт по показам.
// Ключ платформы хранится в sessionStorage (пропадает при закрытии вкладки) и уходит только в заголовке запросов к этому же серверу.
import { h, setChildren, badge, openModal, confirmDialog, toast, field, emptyState, loginScreen } from '/admin/dom.js';
import { icon } from '/admin/icons.js';

const KEY = 'clubos.platform.key';
const root = document.getElementById('app');

// ---------- обращения к серверу ----------
const store = () => { try { return window.sessionStorage; } catch { return null; } };
const getKey = () => store()?.getItem(KEY) || '';
const setKey = (value) => { const s = store(); if (!s) return; if (value) s.setItem(KEY, value); else s.removeItem(KEY); };

class ApiError extends Error { constructor(message, status) { super(message); this.status = status; } }

async function api(method, path, body) {
  let response;
  try {
    response = await fetch(path, {
      method,
      headers: { 'X-Platform-Key': getKey(), ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch { throw new ApiError('Нет связи с сервером', 0); }
  let payload = null;
  try { payload = await response.json(); } catch { /* пустой ответ */ }
  if (!response.ok) {
    if (response.status === 401) { setKey(''); showLogin('Ключ платформы не подошёл. Введите заново.'); }
    const detail = payload && payload.detail;
    const text = typeof detail === 'string' ? detail
      : Array.isArray(detail) ? detail.map((d) => String(d.msg || '').replace(/^Value error, /, '')).filter(Boolean).join('; ') || 'Проверьте введённые данные'
      : `Ошибка сервера (${response.status})`;
    throw new ApiError(text, response.status);
  }
  return payload;
}
const get = (path) => api('GET', path);
const post = (path, body) => api('POST', path, body === undefined ? {} : body);
const patch = (path, body) => api('PATCH', path, body);
const del = (path) => api('DELETE', path);

// ---------- форматирование ----------
const two = (n) => String(n).padStart(2, '0');
const hoursText = (h2) => `${Number(h2).toLocaleString('ru-RU')} ч`;
/** Дата с сервера (UTC без пометки пояса) в местном времени браузера владельца. */
function localStamp(value) {
  if (!value) return '';
  const d = new Date(/(Z|[+-]\d\d:?\d\d)$/.test(value) ? value : `${value}Z`);
  return Number.isNaN(d.getTime()) ? '' : `${two(d.getDate())}.${two(d.getMonth() + 1)}.${d.getFullYear()} ${two(d.getHours())}:${two(d.getMinutes())}`;
}
const toInput = (value) => {
  if (!value) return '';
  const d = new Date(/(Z|[+-]\d\d:?\d\d)$/.test(value) ? value : `${value}Z`);
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())}T${two(d.getHours())}:${two(d.getMinutes())}`;
};
const fromInput = (value) => (value ? new Date(value).toISOString() : null);
const dayInput = (d) => `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())}`;

// ---------- вход ----------
function showLogin(message = '') {
  stopTimers();
  const key = h('input', { type: 'password', autocomplete: 'off', required: true, placeholder: 'ключ платформы' });
  const error = h('div', { class: 'error-text', role: 'alert' }, message);
  const form = h('form', {
    onsubmit: async (ev) => {
      ev.preventDefault();
      error.textContent = '';
      setKey(key.value.trim());
      try { await get('/api/platform/clubs'); key.value = ''; boot(); } catch (e) { if (e.status !== 401) error.textContent = e.message; }
    },
  }, field('Ключ платформы', key), error, h('button', { class: 'btn btn-primary', type: 'submit' }, 'Войти'));
  loginScreen(root, {
    brand: 'ClubOS, панель владельца', title: 'Платформа',
    intro: 'Ключ платформы даёт доступ ко всем клубам. Не передавайте его и не открывайте эту страницу на чужих компьютерах.', form,
  });
  key.focus();
}

// ---------- каркас ----------
const TABS = [['ads', 'Реклама'], ['clubs', 'Клубы'], ['report', 'Отчёт по показам']];
let timers = [];
function stopTimers() { timers.forEach(clearInterval); timers = []; }

function route() {
  const id = (location.hash.match(/^#\/(\w+)/) || [])[1];
  return TABS.find(([key]) => key === id) || TABS[0];
}

function shell() {
  const nav = h('nav', { class: 'nav', 'aria-label': 'Разделы' }, TABS.map(([id, title]) => h('a', { href: `#/${id}`, dataset: { tab: id } }, icon(id), h('span', { class: 'nav-label' }, title))));
  setChildren(root, h('div', { class: 'layout' },
    h('aside', { class: 'sidebar' },
      h('div', { class: 'side-head' }, h('span', { class: 'brand' }, 'ClubOS'), h('span', { class: 'club-name' }, 'Платформа')),
      nav,
      h('div', { class: 'side-foot' }, h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => { setKey(''); showLogin(); } }, 'Выйти'))),
    h('main', { id: 'view' })));
  render();
}

function render() {
  stopTimers();
  const [id, title] = route();
  for (const link of document.querySelectorAll('.nav a')) link.classList.toggle('active', link.dataset.tab === id);
  const main = document.getElementById('view');
  setChildren(main);
  ({ ads: adsView, clubs: clubsView, report: reportView })[id](main);
  document.title = `${title} | ClubOS платформа`;
}

async function boot() {
  try { await get('/api/platform/clubs'); shell(); } catch (e) { if (e.status !== 401) showLogin(e.message); }
}
window.addEventListener('hashchange', () => { if (getKey() && document.getElementById('view')) render(); });

// ---------- реклама ----------
const MAX_IMAGE = 2 * 1024 * 1024;
const DURATIONS = [5, 8, 10, 15, 20, 30, 45, 60];
const thumbs = new Map();   // "id:версия" -> адрес картинки data: (чтобы не скачивать заново при каждом обновлении списка)

function readAsDataUrl(blob) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(new Error('Не удалось прочитать картинку'));
    reader.readAsDataURL(blob);
  });
}

/** Картинка баннера с сервера: запрос идёт с ключом платформы, а на страницу попадает как data:-адрес (CSP других источников не разрешает). */
async function bannerDataUrl(ad) {
  const key = `${ad.id}:${ad.version}`;
  if (thumbs.has(key)) return thumbs.get(key);
  const response = await fetch(`/api/platform/ads/${ad.id}/image`, { headers: { 'X-Platform-Key': getKey() } });
  if (!response.ok) throw new ApiError('Картинка не загрузилась', response.status);
  const url = await readAsDataUrl(await response.blob());
  thumbs.set(key, url);
  return url;
}

async function uploadBanner(adId, file) {
  let response;
  try {
    response = await fetch(`/api/platform/ads/${adId}/image`, { method: 'PUT', headers: { 'X-Platform-Key': getKey(), 'Content-Type': file.type || 'application/octet-stream' }, body: file });
  } catch { throw new ApiError('Нет связи с сервером', 0); }
  if (!response.ok) {
    let detail = '';
    try { detail = (await response.json()).detail; } catch { /* не JSON */ }
    throw new ApiError(typeof detail === 'string' && detail ? detail : `Не удалось загрузить картинку (${response.status})`, response.status);
  }
}

function adsView(main) {
  const box = h('div', { class: 'table-wrap' });
  main.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Реклама'), h('span', { class: 'spacer' }),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: () => editAd(null) }, '+ Новый баннер')),
    h('p', { class: 'muted small' }, 'Баннер, который вам прислал рекламодатель, показывается на весь экран на свободных ПК в тех клубах, где реклама включена (раздел «Клубы»); когда гость нажимает кнопку, открывается форма входа. '
      + 'Если баннеров несколько, они сменяют друг друга по очереди, каждый столько секунд, сколько вы укажете. Лучше всего подходит картинка 1920 x 1080, то есть 16 к 9 (PNG или JPEG, до 2 МБ). '
      + 'Картинка другой формы (обычный баннер 2,5 к 1 или 3 к 1) целиком помещается по центру, свободные поля заливаются продолжением её фона или остаются тёмными. '
      + 'Под баннером гость видит пометку «РЕКЛАМА», рекламодателя и erid, если вы их указали: маркировка обязательна по закону, erid получите у рекламодателя.'),
    box);
  let list = [];

  async function load() {
    try { list = await get('/api/platform/ads'); } catch (e) { toast(e.message, 'error'); return; }
    if (list.length === 0) { setChildren(box, emptyState('Баннеров пока нет. Добавьте первый.')); return; }
    setChildren(box, h('table', {},
      h('thead', {}, h('tr', {}, ['Баннер', 'Рекламодатель', 'Когда показывать', 'Секунд', 'Порядок', 'Показано', 'Статус', ''].map((t) => h('th', {}, t)))),
      h('tbody', {}, list.map((a) => {
        const thumb = h('div', { class: 'banner-thumb' }, a.has_image ? '' : 'нет картинки');
        if (a.has_image) bannerDataUrl(a).then((url) => setChildren(thumb, h('img', { src: url, alt: a.text }))).catch(() => { thumb.textContent = 'ошибка'; });
        return h('tr', { class: a.active ? '' : 'dim' },
          h('td', {}, h('div', { class: 'banner-cell' }, thumb, h('div', {}, h('b', {}, a.text),
            a.has_image ? h('div', { class: 'small muted' }, `${a.width} x ${a.height}`) : h('div', { class: 'small', style: 'color:var(--warn)' }, 'без картинки не показывается'),
            a.erid ? h('div', { class: 'small muted' }, `erid: ${a.erid}`) : null))),
          h('td', { class: 'wrap muted' }, a.advertiser || '-'),
          h('td', { class: 'muted' }, a.starts_at || a.ends_at ? `${a.starts_at ? `с ${localStamp(a.starts_at)}` : 'с начала'}${a.ends_at ? ` по ${localStamp(a.ends_at)}` : ''}` : 'всегда'),
          h('td', { class: 'muted' }, String(a.duration_seconds)),
          h('td', { class: 'muted' }, String(a.sort_order)),
          h('td', {}, hoursText(a.shown_hours || 0)),
          h('td', {}, !a.active ? badge('выключен') : a.has_image ? badge('идёт', 'ok') : badge('нужна картинка', 'warn')),
          h('td', { class: 'right' },
            h('button', { class: 'btn btn-sm', type: 'button', onclick: () => editAd(a) }, 'Изменить'), ' ',
            h('button', { class: 'btn btn-sm', type: 'button', onclick: () => toggle(a) }, a.active ? 'Выключить' : 'Включить'), ' ',
            h('button', { class: 'btn btn-danger btn-sm', type: 'button', onclick: () => remove(a) }, 'Удалить')));
      }))));
  }

  async function toggle(a) {
    try { await patch(`/api/platform/ads/${a.id}`, { active: !a.active }); load(); } catch (e) { toast(e.message, 'error'); }
  }

  async function remove(a) {
    const ok = await confirmDialog({ title: 'Удалить баннер?', text: 'Если по нему уже есть показы, система не даст удалить: выключите его, и отчёт сохранится.', okText: 'Удалить', danger: true });
    if (!ok) return;
    try { await del(`/api/platform/ads/${a.id}`); toast('Удалено', 'ok'); load(); } catch (e) { toast(e.message, 'error'); }
  }

  function editAd(existing) {
    const a = existing || { text: '', advertiser: '', erid: '', active: true, starts_at: null, ends_at: null, sort_order: 100, duration_seconds: 10 };
    let file = null;
    const name = h('input', { maxLength: 200, required: true, value: a.text, placeholder: 'Мама Рома, октябрь (видно только вам)' });
    const picker = h('input', { type: 'file', accept: 'image/png,image/jpeg' });
    const pictureNote = h('div', { class: 'small muted' }, existing && existing.has_image ? `Сейчас: ${a.width} x ${a.height}. Выберите файл, чтобы заменить.` : 'PNG или JPEG, до 2 МБ, лучше всего 1920 x 1080 (16 к 9).');
    const image = h('div', { class: 'banner-preview' }, existing && existing.has_image ? 'загружаем…' : 'картинка не выбрана');
    const advertiser = h('input', { maxLength: 120, value: a.advertiser || '', placeholder: 'ООО «Мама Рома», ИНН 7700000000' });
    const erid = h('input', { maxLength: 80, value: a.erid || '', placeholder: 'токен маркировки, если есть' });
    const seconds = h('select', {}, DURATIONS.map((n) => h('option', { value: n, selected: n === a.duration_seconds }, `${n} секунд`)));
    const start = h('input', { type: 'datetime-local', value: toInput(a.starts_at) });
    const end = h('input', { type: 'datetime-local', value: toInput(a.ends_at) });
    const order = h('input', { type: 'number', min: 0, max: 10000, step: 1, value: a.sort_order });
    const active = h('input', { type: 'checkbox', checked: a.active });
    const caption = h('div', { class: 'ad-caption' }, '');
    const error = h('div', { class: 'error-text', role: 'alert' });

    if (existing && existing.has_image) bannerDataUrl(existing).then((url) => setChildren(image, h('img', { src: url, alt: '' }))).catch(() => { image.textContent = 'не удалось показать'; });

    function sync() {
      caption.textContent = ['РЕКЛАМА', advertiser.value.trim(), erid.value.trim() ? `erid: ${erid.value.trim()}` : ''].filter(Boolean).join('   ');
    }
    advertiser.addEventListener('input', sync);
    erid.addEventListener('input', sync);
    sync();

    picker.addEventListener('change', async () => {
      error.textContent = '';
      const chosen = picker.files && picker.files[0];
      if (!chosen) return;
      if (!['image/png', 'image/jpeg'].includes(chosen.type)) { error.textContent = 'Нужна картинка PNG или JPEG'; picker.value = ''; return; }
      if (chosen.size > MAX_IMAGE) { error.textContent = 'Файл больше 2 МБ: сожмите картинку'; picker.value = ''; return; }
      file = chosen;
      const url = await readAsDataUrl(chosen);
      const probe = new Image();
      probe.onload = () => {
        const ratio = probe.naturalWidth / probe.naturalHeight;
        const shape = Math.abs(ratio - 16 / 9) > 0.25 ? '. Пропорции не 16 к 9: на экране останутся свободные поля (по бокам или сверху и снизу)' : '';
        pictureNote.textContent = `Выбрано: ${probe.naturalWidth} x ${probe.naturalHeight}, ${(chosen.size / 1024).toFixed(0)} КБ${shape}`;
      };
      probe.src = url;
      setChildren(image, h('img', { src: url, alt: '' }));
    });

    async function save() {
      error.textContent = '';
      const body = { text: name.value, advertiser: advertiser.value, erid: erid.value, active: active.checked,
        starts_at: fromInput(start.value), ends_at: fromInput(end.value), sort_order: Number(order.value), duration_seconds: Number(seconds.value) };
      if (!body.text.trim()) { error.textContent = 'Введите название баннера'; return; }
      if (!existing && !file) { error.textContent = 'Выберите картинку баннера'; return; }
      try {
        const saved = existing ? await patch(`/api/platform/ads/${existing.id}`, body) : await post('/api/platform/ads', body);
        if (file) await uploadBanner(saved.id, file);
        modal.close();
        toast('Сохранено', 'ok');
        load();
      } catch (e) { error.textContent = e.message; load(); }
    }

    const modal = openModal({
      title: existing ? 'Изменить баннер' : 'Новый баннер', wide: true,
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
        field('Название (видно только вам)', name),
        field('Картинка баннера', picker, null), pictureNote, image,
        h('div', { class: 'row grow' }, field('Рекламодатель', advertiser), field('erid (маркировка)', erid)),
        h('div', { class: 'row grow' }, field('Сколько секунд показывать', seconds), field('Порядок в ротации', order)),
        h('div', { class: 'row grow' }, field('Показывать с (необязательно)', start), field('Показывать до (необязательно)', end)),
        h('label', { class: 'check' }, active, h('span', {}, 'Показывать')),
        h('h3', {}, 'Подпись под баннером'), caption, error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, 'Сохранить')],
    });
  }

  load();
  timers.push(setInterval(() => { if (!document.hidden) load(); }, 30000));
}

// ---------- клубы ----------
function clubsView(main) {
  const box = h('div', { class: 'table-wrap' });
  main.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Клубы')),
    h('p', { class: 'muted small' }, 'Клуб сам включает показ рекламы у себя в админке. Здесь вы можете включить или выключить рекламу за него и задать долю клуба в доходе от рекламы.'),
    box);

  async function load() {
    let clubs;
    try { clubs = await get('/api/platform/clubs'); } catch (e) { toast(e.message, 'error'); return; }
    if (clubs.length === 0) { setChildren(box, emptyState('Клубов пока нет')); return; }
    setChildren(box, h('table', {},
      h('thead', {}, h('tr', {}, ['Клуб', 'Код', 'Статус', 'Реклама', 'Доля клуба', ''].map((t, i) => h('th', { class: i === 4 ? 'right' : '' }, t)))),
      h('tbody', {}, clubs.map((c) => h('tr', {},
        h('td', {}, h('b', {}, c.name)),
        h('td', { class: 'muted' }, c.slug),
        h('td', {}, c.status === 'active' ? badge('работает', 'ok') : badge('приостановлен', 'warn')),
        h('td', {}, c.ads_enabled ? badge('включена', 'ok') : badge('выключена')),
        h('td', { class: 'right' }, `${c.ads_club_share}%`),
        h('td', { class: 'right' },
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => setAds(c, { enabled: !c.ads_enabled }) }, c.ads_enabled ? 'Выключить рекламу' : 'Включить рекламу'), ' ',
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => shareModal(c) }, 'Доля клуба')))))));
  }

  async function setAds(club, body) {
    try { await post(`/api/platform/clubs/${club.id}/ads`, body); load(); } catch (e) { toast(e.message, 'error'); }
  }

  function shareModal(club) {
    const share = h('input', { type: 'number', min: 0, max: 100, step: 1, value: club.ads_club_share, required: true });
    const error = h('div', { class: 'error-text', role: 'alert' });
    async function save() {
      const value = Number(share.value);
      if (!Number.isInteger(value) || value < 0 || value > 100) { error.textContent = 'Доля: целое число от 0 до 100'; return; }
      try { await post(`/api/platform/clubs/${club.id}/ads`, { club_share: value }); modal.close(); toast('Сохранено', 'ok'); load(); } catch (e) { error.textContent = e.message; }
    }
    const modal = openModal({
      title: `Доля клуба «${club.name}»`,
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
        field('Доля клуба в доходе от рекламы, %', share, 'Остальное остаётся платформе. Деньги система не считает: она показывает часы показа в отчёте.'), error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'), h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, 'Сохранить')],
    });
  }

  load();
}

// ---------- отчёт ----------
function reportView(main) {
  const box = h('div', {});
  const today = new Date();
  const since = h('input', { type: 'date', value: dayInput(new Date(today.getTime() - 29 * 86400000)) });
  const until = h('input', { type: 'date', value: dayInput(today) });
  const preset = (days) => () => { since.value = dayInput(new Date(Date.now() - (days - 1) * 86400000)); until.value = dayInput(new Date()); load(); };
  main.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Отчёт по показам')),
    h('div', { class: 'row', style: 'margin-bottom:18px' }, h('div', { class: 'presets' },
      h('button', { class: 'btn btn-sm', type: 'button', onclick: preset(7) }, '7 дней'),
      h('button', { class: 'btn btn-sm', type: 'button', onclick: preset(30) }, '30 дней'), ' '),
    h('label', { class: 'field', style: 'margin:0' }, h('span', {}, 'С'), since),
    h('label', { class: 'field', style: 'margin:0' }, h('span', {}, 'По'), until),
    h('button', { class: 'btn btn-primary', type: 'button', onclick: () => load() }, 'Показать')),
    h('p', { class: 'muted small' }, 'Время считает сервер по опросам ПК, пока на них виден экран блокировки с рекламой. Сутки по времени каждого клуба. '
      + 'Экранное время клуба считается один раз, сколько бы объявлений ни шло в строке.'),
    box);

  async function load() {
    let r;
    try { r = await get(`/api/platform/ads/report?since=${since.value}&until=${until.value}`); } catch (e) { toast(e.message, 'error'); return; }
    setChildren(box,
      h('h2', { class: 'section-title' }, 'По клубам'),
      r.per_club.length === 0 ? emptyState('За этот период показов нет') : h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, ['Клуб', 'Реклама', 'Доля клуба', 'Экранное время'].map((t, i) => h('th', { class: i > 1 ? 'right' : '' }, t)))),
        h('tbody', {}, r.per_club.map((c) => h('tr', {}, h('td', {}, h('b', {}, c.club)), h('td', {}, c.enabled ? badge('включена', 'ok') : badge('выключена')),
          h('td', { class: 'right' }, `${c.club_share}%`), h('td', { class: 'right' }, hoursText(c.screen_hours))))))),
      h('h2', { class: 'section-title' }, 'По объявлениям'),
      r.per_ad.length === 0 ? emptyState('За этот период показов нет') : h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {}, ['Объявление', 'Рекламодатель', 'Всего', 'По клубам'].map((t, i) => h('th', { class: i === 2 ? 'right' : '' }, t)))),
        h('tbody', {}, r.per_ad.map((a) => h('tr', {}, h('td', { class: 'wrap' }, h('b', {}, a.text)), h('td', { class: 'wrap muted' }, a.advertiser || '-'),
          h('td', { class: 'right' }, hoursText(a.hours)),
          h('td', { class: 'wrap muted' }, a.clubs.map((c) => `${c.club}: ${hoursText(c.hours)}`).join('; '))))))));
  }
  load();
}

if (getKey()) boot(); else showLogin();

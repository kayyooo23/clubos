// Точка входа: вход → каркас с вкладками → нужный экран по адресу (#/map, #/guests …). Старая ссылка #/pcs ведёт на карту клуба.
import { h, setChildren, toast } from './dom.js';
import { icon } from './icons.js';
import { get, getToken, logout, onUnauthorized, ApiError } from './api.js';
import { renderLogin } from './views/login.js';
import * as dashboard from './views/dashboard.js';
import * as clubMap from './views/map.js';
import * as guests from './views/guests.js';
import * as sessions from './views/sessions.js';
import * as tariffs from './views/tariffs.js';
import * as ads from './views/ads.js';
import * as apps from './views/apps.js';
import * as sync from './views/sync.js';
import * as bookings from './views/bookings.js';
import * as orders from './views/orders.js';
import * as products from './views/products.js';
import * as report from './views/report.js';
import * as audit from './views/audit.js';

const root = document.getElementById('app');

const TABS = [
  { id: 'dashboard', title: 'Дашборд', view: dashboard },
  { id: 'map', title: 'Карта клуба', view: clubMap },
  { id: 'guests', title: 'Гости', view: guests },
  { id: 'sessions', title: 'Сессии', view: sessions },
  { id: 'bookings', title: 'Бронирование', view: bookings },
  { id: 'orders', title: 'Заказы', view: orders },
  { id: 'tariffs', title: 'Залы и тарифы', view: tariffs },
  { id: 'apps', title: 'Игры', view: apps },
  { id: 'sync', title: 'Синхронизация', view: sync },
  { id: 'products', title: 'Бар', view: products },
  { id: 'ads', title: 'Реклама', view: ads },
  { id: 'report', title: 'Отчёт', view: report },
  { id: 'audit', title: 'Журнал', view: audit },
];

let current = null;   // { unmount }
let ctx = null;       // { club, user }
let inboxTimer = null;
let lastWaiting = 0;
let audio = null;

function route() {
  const raw = (location.hash.match(/^#\/(\w+)/) || [])[1];
  const id = raw === 'pcs' ? 'map' : raw;   // вкладку «Компьютеры» объединили с картой клуба
  return TABS.find((t) => t.id === id) || TABS[0];
}

function showLogin(message = '') {
  unmount();
  stopInbox();
  ctx = null;
  renderLogin(root, { message, onDone: boot });
}

/** Короткий сигнал, когда появился новый заказ или вызов: сотрудник может смотреть в другое окно. Звук необязателен. */
function beep() {
  try {
    audio = audio || new (window.AudioContext || window.webkitAudioContext)();
    if (audio.state === 'suspended') audio.resume();
    const osc = audio.createOscillator();
    const gain = audio.createGain();
    osc.type = 'square';
    osc.frequency.value = 880;
    gain.gain.value = 0.04;
    osc.connect(gain);
    gain.connect(audio.destination);
    osc.start();
    osc.stop(audio.currentTime + 0.18);
  } catch { /* без звука тоже работает */ }
}

/** Раз в несколько секунд спрашиваем, сколько заказов и вызовов ждёт сотрудника: значок на вкладке и сигнал о новых. */
async function pollInbox() {
  if (!ctx) return;
  try {
    const inbox = await get('/api/admin/inbox');
    const total = inbox.orders_open + inbox.calls_open;
    const badgeEl = document.querySelector('.nav .count');
    if (badgeEl) { badgeEl.textContent = String(total); badgeEl.hidden = total === 0; }
    const fresh = inbox.orders_new + inbox.calls_open;
    if (fresh > lastWaiting) {
      beep();
      toast(inbox.calls_open > 0 ? 'Администратора зовут или есть новый заказ' : 'Новый заказ из бара');
    }
    lastWaiting = fresh;
  } catch { /* связь вернётся: попробуем в следующий раз */ }
}

function startInbox() {
  stopInbox();
  lastWaiting = 0;
  pollInbox();
  inboxTimer = setInterval(pollInbox, 5000);
}

function stopInbox() {
  if (inboxTimer) clearInterval(inboxTimer);
  inboxTimer = null;
}

function unmount() {
  if (current && current.unmount) current.unmount();
  current = null;
}

function renderRoute() {
  if (!ctx) return;
  unmount();
  const tab = route();
  for (const link of document.querySelectorAll('.nav a')) link.classList.toggle('active', link.dataset.tab === tab.id);
  const main = document.getElementById('view');
  setChildren(main);
  current = tab.view.mount(main, ctx) || null;
  document.title = `${tab.title} | ClubOS`;
}

function shell() {
  const nav = h('nav', { class: 'nav', 'aria-label': 'Разделы' },
    TABS.map((t) => h('a', { href: `#/${t.id}`, dataset: { tab: t.id } }, icon(t.id), h('span', { class: 'nav-label' }, t.title),
      t.id === 'orders' ? h('span', { class: 'count', hidden: true }, '0') : null)));
  setChildren(root,
    h('div', { class: 'layout' },
      h('aside', { class: 'sidebar no-print' },
        h('div', { class: 'side-head' }, h('span', { class: 'brand' }, 'ClubOS'), h('span', { class: 'club-name' }, ctx.club.name)),
        nav,
        h('div', { class: 'side-foot' },
          h('span', { class: 'muted small' }, ctx.user.phone),
          h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: async () => { await logout(); showLogin(); } }, 'Выйти'))),
      h('main', { id: 'view' })));
  renderRoute();
  startInbox();
}

async function boot() {
  try {
    const me = await get('/api/admin/auth/me');
    ctx = { club: me.club, user: me };
    shell();
  } catch (e) {
    if (e instanceof ApiError && e.status === 401) return showLogin();
    showLogin(e.message);
  }
}

onUnauthorized(() => { if (ctx) showLogin('Сессия завершилась, войдите заново.'); });
window.addEventListener('hashchange', renderRoute);
window.addEventListener('unhandledrejection', (event) => {
  if (event.reason instanceof ApiError) { event.preventDefault(); toast(event.reason.message, 'error'); }
});

if (getToken()) boot(); else showLogin();

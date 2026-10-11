import { h, setChildren, toast } from '../dom.js';
import { get } from '../api.js';
import { money } from '../format.js';

const REFRESH_MS = 10000;

function tile(label, value, hint, href) {
  const body = [h('div', { class: 'label' }, label), h('div', { class: 'value' }, value), hint ? h('div', { class: 'hint' }, hint) : null];
  return href ? h('a', { class: 'stat', href, style: 'display:block;color:inherit;text-decoration:none' }, body) : h('div', { class: 'stat' }, body);
}

export function mount(root) {
  const box = h('div', {});
  root.append(h('div', { class: 'page-head' }, h('h1', {}, 'Дашборд'), h('span', { class: 'spacer' }),
    h('span', { class: 'muted small' }, 'Сегодня, по времени клуба')), box);

  async function load() {
    let d;
    try { d = await get('/api/admin/dashboard'); } catch (e) { toast(e.message, 'error'); return; }
    const offline = d.pcs_total - d.pcs_online;
    const attention = [];
    if (offline > 0) attention.push(`нет связи: ${offline}`);
    if (d.pcs_maintenance > 0) attention.push(`на обслуживании: ${d.pcs_maintenance}`);
    if (d.pcs_config_changed > 0) attention.push(`изменена конфигурация: ${d.pcs_config_changed}`);
    setChildren(box,
      h('div', { class: 'stats' },
        tile('Загрузка зала', `${d.load_percent}%`, `играют ${d.pcs_playing} из ${d.pcs_total} ПК`, '#/map'),
        tile('Выручка за игру', money(d.games_revenue_today), `сессий закрыто: ${d.sessions_today}`, '#/sessions'),
        tile('Бар', money(d.shop_revenue_today), `заказов: ${d.shop_orders_today}`),
        tile('Принято наличными', money(d.cash_in_today), 'пополнения балансов за сегодня', '#/report'),
        tile('Новых гостей', String(d.new_guests_today), 'зарегистрировались сегодня', '#/guests'),
        tile('Ждут сотрудника', String(d.orders_open + d.calls_open), `заказов: ${d.orders_open}, вызовов: ${d.calls_open}`, '#/orders')),
      h('h2', { class: 'section-title' }, 'Нужно внимание'),
      attention.length === 0
        ? h('div', { class: 'empty' }, 'Всё в порядке: все ПК на связи, обслуживания и изменений нет')
        : h('div', { class: 'warn-box' }, attention.join(', '), ' ', h('a', { href: '#/map' }, 'Открыть карту клуба')));
  }

  const timer = setInterval(() => { if (!document.hidden) load(); }, REFRESH_MS);
  load();
  return { unmount() { clearInterval(timer); } };
}

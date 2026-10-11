import { h, setChildren, badge, confirmDialog, toast } from '../dom.js';
import { get, post } from '../api.js';

export function mount(root) {
  const box = h('div', {});
  root.append(h('div', { class: 'page-head' }, h('h1', {}, 'Реклама')), box);

  async function load() {
    let info;
    try { info = await get('/api/admin/ads'); } catch (e) { toast(e.message, 'error'); return; }
    const hours = Number(info.screen_hours_30d).toLocaleString('ru-RU');
    setChildren(box,
      h('div', { class: 'stats' },
        h('div', { class: 'stat' }, h('div', { class: 'label' }, 'Реклама в клубе'), h('div', { class: 'value' }, info.enabled ? 'Включена' : 'Выключена'),
          h('div', { class: 'hint' }, info.enabled ? 'баннер на весь экран свободных ПК' : 'экраны блокировки без рекламы')),
        h('div', { class: 'stat' }, h('div', { class: 'label' }, 'Доля клуба'), h('div', { class: 'value' }, `${info.club_share}%`),
          h('div', { class: 'hint' }, 'от дохода за рекламу в вашем клубе')),
        h('div', { class: 'stat' }, h('div', { class: 'label' }, 'Показано за 30 дней'), h('div', { class: 'value' }, `${hours} ч`),
          h('div', { class: 'hint' }, 'экранное время с рекламой, по всем ПК')),
        h('div', { class: 'stat' }, h('div', { class: 'label' }, 'Баннеров сейчас'), h('div', { class: 'value' }, String(info.active_count)),
          h('div', { class: 'hint' }, 'идут по очереди, когда реклама включена'))),
      h('p', { class: 'muted' }, 'Если включить, на свободных ПК клуба на весь экран пойдут баннеры партнёров ClubOS (если их несколько, они сменяют друг друга) с пометкой «РЕКЛАМА». Гость нажимает кнопку мыши, и баннер уступает форме входа, поэтому вход и регистрация ничем не мешают. Во время игры и в меню гостя баннера нет. Клуб получает указанную долю дохода от рекламы, '
        + 'время показа считает сервер, отчёт ведёт платформа. Выключить можно в любой момент.'),
      h('div', { class: 'actions', style: 'display:flex;gap:10px;margin-top:18px' },
        h('button', { class: info.enabled ? 'btn btn-danger' : 'btn btn-primary', type: 'button', onclick: () => toggle(info) },
          info.enabled ? 'Выключить рекламу' : 'Включить рекламу'),
        info.enabled ? badge('включена', 'ok') : badge('выключена')));
  }

  async function toggle(info) {
    if (info.enabled) {
      const ok = await confirmDialog({ title: 'Выключить рекламу?', text: 'Баннеры пропадут с экранов блокировки. Доход от рекламы перестанет начисляться.', okText: 'Выключить', danger: true });
      if (!ok) return;
    }
    try {
      await post('/api/admin/ads/enabled', { enabled: !info.enabled });
      toast(info.enabled ? 'Реклама выключена' : 'Реклама включена', 'ok');
      load();
    } catch (e) { toast(e.message, 'error'); }
  }

  load();
}

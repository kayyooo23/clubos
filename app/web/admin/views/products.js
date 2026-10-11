import { h, setChildren, badge, openModal, confirmDialog, toast, field, emptyState } from '../dom.js';
import { get, post, patch, del } from '../api.js';
import { money } from '../format.js';

const CATEGORIES = ['Напитки', 'Еда', 'Снеки', 'Сладкое', 'Энергетики', 'Другое'];

export function mount(root) {
  const box = h('div', { class: 'table-wrap' });
  root.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Бар'), h('span', { class: 'spacer' }),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: () => editModal(null) }, '+ Добавить товар')),
    h('p', { class: 'muted small' }, 'Меню, которое гости видят на своём ПК. Заказ оплачивается с баланса сразу, а здесь, в разделе «Заказы», '
      + 'вы видите, что и за какой ПК нести. Скрытый товар остаётся в списке, но из меню пропадает. Товар, который уже заказывали, удалить нельзя, его можно скрыть.'),
    box);

  let list = [];

  async function load() {
    try {
      list = await get('/api/admin/products');
    } catch (e) { toast(e.message, 'error'); return; }
    if (list.length === 0) { setChildren(box, emptyState('В баре пока пусто. Добавьте первый товар.')); return; }
    setChildren(box, h('table', {},
      h('thead', {}, h('tr', {}, ['Название', 'Категория', 'Цена', 'Статус', ''].map((t, i) => h('th', { class: i === 2 ? 'right' : '' }, t)))),
      h('tbody', {}, list.map((p) => h('tr', { class: p.active ? '' : 'dim' },
        h('td', {}, h('b', {}, p.name)),
        h('td', { class: 'muted' }, p.category),
        h('td', { class: 'right' }, money(p.price)),
        h('td', {}, p.active ? badge('в меню', 'ok') : badge('скрыт')),
        h('td', { class: 'right' },
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => editModal(p) }, 'Изменить'), ' ',
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => toggle(p) }, p.active ? 'Скрыть' : 'Показать'), ' ',
          h('button', { class: 'btn btn-danger btn-sm', type: 'button', onclick: () => remove(p) }, 'Удалить')))))));
  }

  async function toggle(p) {
    try {
      await patch(`/api/admin/products/${p.id}`, { active: !p.active });
      toast(p.active ? `«${p.name}» скрыт из меню` : `«${p.name}» снова в меню`, 'ok');
      load();
    } catch (e) { toast(e.message, 'error'); }
  }

  async function remove(p) {
    const ok = await confirmDialog({ title: `Удалить «${p.name}»?`, text: 'Товар исчезнет из меню. Если его уже заказывали, система не даст удалить: скройте его.', okText: 'Удалить', danger: true });
    if (!ok) return;
    try {
      await del(`/api/admin/products/${p.id}`);
      toast('Удалено', 'ok');
      load();
    } catch (e) { toast(e.message, 'error'); }
  }

  function editModal(existing) {
    const p = existing || { name: '', category: 'Напитки', price: '', sort_order: 100, active: true };
    const name = h('input', { maxLength: 80, required: true, value: p.name, placeholder: 'например, Кола 0,5 л' });
    const categories = [...new Set([...CATEGORIES, ...list.map((x) => x.category)])];
    const category = h('input', { maxLength: 40, required: true, value: p.category, list: 'product-cat-list' });
    const catList = h('datalist', { id: 'product-cat-list' }, categories.map((c) => h('option', { value: c })));
    const price = h('input', { type: 'number', min: 1, step: 1, inputMode: 'numeric', required: true, value: p.price });
    const order = h('input', { type: 'number', min: 0, max: 10000, step: 1, inputMode: 'numeric', value: p.sort_order });
    const active = h('input', { type: 'checkbox', checked: p.active });
    const error = h('div', { class: 'error-text', role: 'alert' });

    async function save() {
      error.textContent = '';
      const body = { name: name.value.trim(), category: category.value.trim(), price: Number(price.value),
        sort_order: Number(order.value), active: active.checked };
      if (!body.name) { error.textContent = 'Введите название'; return; }
      if (!Number.isInteger(body.price) || body.price <= 0) { error.textContent = 'Цена: целое число рублей больше нуля'; return; }
      try {
        if (existing) await patch(`/api/admin/products/${existing.id}`, body); else await post('/api/admin/products', body);
        modal.close();
        toast(existing ? 'Сохранено' : `«${body.name}» добавлен в меню`, 'ok');
        load();
      } catch (e) { error.textContent = e.message; }
    }

    const modal = openModal({
      title: existing ? 'Изменить товар' : 'Новый товар',
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
        field('Название', name),
        h('div', { class: 'row grow' }, h('label', { class: 'field' }, h('span', {}, 'Категория'), category, catList), field('Цена, ₽', price)),
        field('Порядок в меню', order, 'Меньше число, раньше в списке'),
        h('label', { class: 'check' }, active, h('span', {}, 'Показывать в меню')),
        error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, existing ? 'Сохранить' : 'Добавить')],
    });
  }

  load();
}

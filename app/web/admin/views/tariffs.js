import { h, setChildren, badge, openModal, confirmDialog, toast, field, emptyState } from '../dom.js';
import { get, post, del } from '../api.js';
import { money, kindName, tariffDescribe } from '../format.js';

export function mount(root) {
  const box = h('div', { class: 'table-wrap' });
  root.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Тарифы'), h('span', { class: 'spacer' }),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: () => addModal() }, '+ Новый тариф')),
    h('p', { class: 'muted small' }, 'Гости выбирают тариф на экране ПК. Отключённый тариф исчезает из выбора, а прошлые сессии по нему сохраняются в отчётах.'),
    box);

  async function load() {
    try {
      const list = await get('/api/admin/tariffs');
      if (list.length === 0) { setChildren(box, emptyState('Тарифов нет, гости не смогут начать игру. Добавьте первый тариф.')); return; }
      setChildren(box, h('table', {},
        h('thead', {}, h('tr', {}, ['Название', 'Тип', 'Условия', ''].map((t) => h('th', {}, t)))),
        h('tbody', {}, list.map((t) => h('tr', {},
          h('td', {}, h('b', {}, t.name)),
          h('td', {}, badge(kindName(t.kind), t.kind === 'night' ? 'accent' : '')),
          h('td', { class: 'wrap' }, tariffDescribe(t)),
          h('td', { class: 'right' }, h('button', { class: 'btn btn-danger btn-sm', type: 'button', onclick: () => disable(t) }, 'Отключить')))))));
    } catch (e) { toast(e.message, 'error'); }
  }

  async function disable(t) {
    const ok = await confirmDialog({ title: `Отключить тариф «${t.name}»?`, text: 'Гости больше не увидят его при выборе. Уже идущие сессии не затрагиваются.', okText: 'Отключить', danger: true });
    if (!ok) return;
    await del(`/api/admin/tariffs/${t.id}`);
    toast('Тариф отключён', 'ok');
    load();
  }

  function addModal() {
    const name = h('input', { maxLength: 80, placeholder: 'например, «3 часа» или «Ночь»', required: true });
    const kind = h('select', {}, ['hourly', 'package', 'night'].map((k) => h('option', { value: k }, kindName(k))));
    const price = h('input', { type: 'number', min: 1, step: 1, inputMode: 'numeric', required: true });
    const minutes = h('input', { type: 'number', min: 1, step: 1, inputMode: 'numeric', placeholder: 'например, 180' });
    const hours = (value) => h('select', {}, Array.from({ length: 24 }, (_, i) => h('option', { value: i, selected: i === value }, `${String(i).padStart(2, '0')}:00`)));
    const start = hours(23);
    const end = hours(8);
    const priceLabel = h('span', {}, 'Цена за час, ₽');
    const minutesField = field('Длительность пакета, минут', minutes, '180 минут = 3 часа');
    const nightField = h('div', { class: 'row grow' }, field('Ночь начинается', start), field('Ночь заканчивается', end));
    const error = h('div', { class: 'error-text', role: 'alert' });
    const explain = h('p', { class: 'small muted' });

    function sync() {
      minutesField.hidden = kind.value !== 'package';
      nightField.hidden = kind.value !== 'night';
      priceLabel.textContent = kind.value === 'hourly' ? 'Цена за час, ₽' : kind.value === 'package' ? 'Цена пакета, ₽' : 'Цена за всю ночь, ₽';
      explain.textContent = {
        hourly: 'Гость платит за фактическое время, с баланса списывается поминутно. Играть можно, пока хватает денег.',
        package: 'Фиксированная цена за пакет часов: списывается сразу при старте, при досрочной остановке не возвращается.',
        night: 'Фиксированная цена за ночь: стартовать можно только внутри ночного окна, сессия кончается с его концом (время клуба).',
      }[kind.value];
    }
    kind.addEventListener('change', sync);
    sync();

    async function save() {
      error.textContent = '';
      const body = { name: name.value.trim(), kind: kind.value, price: Number(price.value) };
      if (!body.name) { error.textContent = 'Введите название'; return; }
      if (!Number.isInteger(body.price) || body.price <= 0) { error.textContent = 'Цена: целое число рублей больше нуля'; return; }
      if (kind.value === 'package') {
        body.package_minutes = Number(minutes.value);
        if (!Number.isInteger(body.package_minutes) || body.package_minutes <= 0) { error.textContent = 'Укажите длительность пакета в минутах'; return; }
      }
      if (kind.value === 'night') {
        body.night_start = Number(start.value);
        body.night_end = Number(end.value);
        if (body.night_start === body.night_end) { error.textContent = 'Начало и конец ночи должны отличаться'; return; }
      }
      try {
        await post('/api/admin/tariffs', body);
        modal.close();
        toast(`Тариф «${body.name}» добавлен (${money(body.price)})`, 'ok');
        load();
      } catch (e) { error.textContent = e.message; }
    }

    const modal = openModal({
      title: 'Новый тариф',
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
        field('Название', name), field('Тип', kind), explain,
        h('label', { class: 'field' }, priceLabel, price), minutesField, nightField, error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, 'Добавить')],
    });
  }

  load();
}

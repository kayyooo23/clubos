import { h, setChildren, badge, openModal, confirmDialog, toast, field, emptyState } from '../dom.js';
import { get, post, patch, del, uploadFile, imageDataUrl } from '../api.js';
import { pickProgram } from './programpicker.js';

const TABS = { games: 'Игры', apps: 'Программы' };
const AGES = [['', 'Не указан'], ['0', '0+'], ['6', '6+'], ['12', '12+'], ['16', '16+'], ['18', '18+']];
const CATEGORIES = ['Шутеры', 'MOBA', 'RPG', 'Стратегии', 'Симуляторы', 'Спорт и гонки', 'Экшены', 'Файтинги', 'Браузеры', 'Общение', 'Другое'];

const MAX_COVER = 2 * 1024 * 1024;
const SOURCES = { steam: 'подтянута из Steam', manual: 'загружена вручную', program: 'взята с ПК клуба' };

const launchText = (a) => (a.launch_kind === 'steam' ? `Steam: ${a.launch_target}` : a.launch_target);

export function mount(root, ctx) {
  const box = h('div', { class: 'table-wrap' });
  root.append(
    h('div', { class: 'page-head' }, h('h1', {}, 'Игры'), h('span', { class: 'spacer' }),
      h('button', { class: 'btn', type: 'button', onclick: () => steamCovers() }, 'Подтянуть обложки из Steam'), ' ',
      h('button', { class: 'btn btn-primary', type: 'button', onclick: () => editModal(null) }, '+ Добавить игру')),
    h('p', { class: 'muted small' }, 'Этот список гости видят в меню на каждом ПК клуба. Игры из Steam запускаются через Steam и обновляются им. '
      + 'Игру из другого лаунчера можно добавить по пути к файлу. Скрытая запись остаётся здесь, но из меню пропадает. '
      + 'У игры в меню есть обложка (вертикальный постер, лучше 600 x 900, PNG или JPEG до 2 МБ): её можно подтянуть из Steam или загрузить свою.'),
    box);

  let list = [];
  const thumbs = new Map();   // "id:версия" -> адрес картинки data: (не скачиваем заново при каждом обновлении списка)

  function thumb(a) {
    const box = h('button', { class: `cover-thumb${a.tab === 'apps' ? ' is-icon' : ''}`, type: 'button', title: a.tab === 'apps' ? 'Иконка' : 'Обложка', onclick: () => coverModal(a) }, a.cover_version ? '' : 'нет');
    if (a.cover_version) {
      const key = `${a.id}:${a.cover_version}`;
      const show = (url) => setChildren(box, h('img', { src: url, alt: '' }));
      if (thumbs.has(key)) show(thumbs.get(key));
      else imageDataUrl(`/api/admin/apps/${a.id}/cover`).then((url) => { thumbs.set(key, url); show(url); }).catch(() => { box.textContent = 'ошибка'; });
    }
    return box;
  }

  async function steamCovers() {
    const ok = await confirmDialog({
      title: 'Подтянуть обложки из Steam?',
      text: 'Сервер запросит картинки в каталоге Steam для игр из Steam, у которых ещё нет обложки. Загруженные вручную обложки не заменяются. Может занять до минуты.',
      okText: 'Подтянуть',
    });
    if (!ok) return;
    toast('Запрашиваем обложки…');
    try {
      const r = await post('/api/admin/apps/covers/steam');
      const parts = [`добавлено ${r.added}`];
      if (r.not_found) parts.push(`у ${r.not_found} Steam картинки не нашёл`);
      if (r.left) parts.push(`осталось ${r.left}: нажмите ещё раз`);
      toast(`Обложки: ${parts.join(', ')}`, r.added ? 'ok' : '');
      load();
    } catch (e) { toast(e.message, 'error'); }
  }

  function coverModal(a) {
    let current = a;
    const preview = h('div', { class: `cover-preview${a.tab === 'apps' ? ' is-icon' : ''}` }, a.tab === 'apps' ? 'иконки нет' : 'обложки нет');
    const note = h('div', { class: 'small muted' });
    const picker = h('input', { type: 'file', accept: 'image/png,image/jpeg' });
    const error = h('div', { class: 'error-text', role: 'alert' });
    const removeButton = h('button', { class: 'btn btn-danger btn-sm', type: 'button', onclick: () => removeCover() }, 'Убрать обложку');

    function render() {
      removeButton.style.display = current.cover_version ? '' : 'none';
      note.textContent = current.cover_version
        ? `Сейчас: ${SOURCES[current.cover_source] || 'есть'}. Выберите файл, чтобы заменить.`
        : a.tab === 'apps' ? 'Иконки нет: в меню будет плитка с буквой. Иконка подставляется сама, когда этот путь есть в списке программ, найденных на ПК («Выбрать на ПК» → «Обновить список»).'
          : 'Обложки нет: в меню будет плитка с буквой. Лучше всего вертикальный постер 600 x 900, PNG или JPEG до 2 МБ.';
      if (!current.cover_version) { setChildren(preview, a.tab === 'apps' ? 'иконки нет' : 'обложки нет'); return; }
      imageDataUrl(`/api/admin/apps/${current.id}/cover`).then((url) => setChildren(preview, h('img', { src: url, alt: '' }))).catch(() => { preview.textContent = 'не удалось показать'; });
    }

    picker.addEventListener('change', async () => {
      error.textContent = '';
      const file = picker.files && picker.files[0];
      if (!file) return;
      if (!['image/png', 'image/jpeg'].includes(file.type)) { error.textContent = 'Нужна картинка PNG или JPEG'; picker.value = ''; return; }
      if (file.size > MAX_COVER) { error.textContent = 'Файл больше 2 МБ: сожмите картинку'; picker.value = ''; return; }
      try {
        current = await uploadFile(`/api/admin/apps/${a.id}/cover`, file);
        toast('Обложка загружена', 'ok');
        render();
        load();
      } catch (e) { error.textContent = e.message; }
      picker.value = '';
    });

    async function removeCover() {
      error.textContent = '';
      try {
        await del(`/api/admin/apps/${a.id}/cover`);
        current = { ...current, cover_version: null, cover_source: null };
        toast('Обложка убрана', 'ok');
        render();
        load();
      } catch (e) { error.textContent = e.message; }
    }

    render();
    const modal = openModal({
      title: `${a.tab === 'apps' ? 'Иконка' : 'Обложка'}: ${a.title}`,
      content: h('div', {}, field('Картинка', picker), note, preview, h('div', { style: 'margin-top:12px' }, removeButton), error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Закрыть')],
    });
  }

  async function load() {
    try {
      list = await get('/api/admin/apps');
    } catch (e) { toast(e.message, 'error'); return; }
    if (list.length === 0) { setChildren(box, emptyState('В меню пока пусто. Добавьте первую игру или программу.')); return; }
    setChildren(box, h('table', {},
      h('thead', {}, h('tr', {}, ['Картинка', 'Название', 'Вкладка', 'Категория', 'Возраст', 'Запуск', 'Статус', ''].map((t) => h('th', {}, t)))),
      h('tbody', {}, list.map((a) => h('tr', { class: a.active ? '' : 'dim' },
        h('td', {}, thumb(a)),
        h('td', {}, h('b', {}, a.title)),
        h('td', { class: 'muted' }, TABS[a.tab] || a.tab),
        h('td', {}, a.category),
        h('td', { class: 'muted' }, a.age_rating == null ? '-' : `${a.age_rating}+`),
        h('td', { class: 'wrap muted' }, launchText(a)),
        h('td', {}, a.active ? badge('в меню', 'ok') : badge('скрыта')),
        h('td', { class: 'right' },
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => editModal(a) }, 'Изменить'), ' ',
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => toggle(a) }, a.active ? 'Скрыть' : 'Показать'), ' ',
          h('button', { class: 'btn btn-danger btn-sm', type: 'button', onclick: () => remove(a) }, 'Удалить')))))));
  }

  async function toggle(a) {
    try {
      await patch(`/api/admin/apps/${a.id}`, { active: !a.active });
      toast(a.active ? `«${a.title}» скрыта из меню` : `«${a.title}» снова в меню`, 'ok');
      load();
    } catch (e) { toast(e.message, 'error'); }
  }

  async function remove(a) {
    const ok = await confirmDialog({ title: `Удалить «${a.title}»?`, text: 'Запись исчезнет из меню на всех ПК клуба. Если нужно только временно убрать игру, лучше скрыть её.', okText: 'Удалить', danger: true });
    if (!ok) return;
    try {
      await del(`/api/admin/apps/${a.id}`);
      toast('Удалено', 'ok');
      load();
    } catch (e) { toast(e.message, 'error'); }
  }

  function editModal(existing) {
    const a = existing || { title: '', tab: 'games', category: 'Шутеры', age_rating: null, launch_kind: 'steam', launch_target: '', sort_order: 100, active: true };
    const title = h('input', { maxLength: 80, required: true, value: a.title, placeholder: 'например, Dota 2' });
    const tab = h('select', {}, Object.entries(TABS).map(([value, text]) => h('option', { value, selected: value === a.tab }, text)));
    const categories = [...new Set([...CATEGORIES, ...list.map((x) => x.category)])];
    const category = h('input', { maxLength: 40, required: true, value: a.category, list: 'cat-list' });
    const catList = h('datalist', { id: 'cat-list' }, categories.map((c) => h('option', { value: c })));
    const age = h('select', {}, AGES.map(([value, text]) => h('option', { value, selected: value === (a.age_rating == null ? '' : String(a.age_rating)) }, text)));
    const kind = h('select', {}, [['steam', 'Steam (по номеру игры)'], ['exe', 'Файл на ПК (путь к exe)']].map(([value, text]) => h('option', { value, selected: value === a.launch_kind }, text)));
    const target = h('input', { maxLength: 260, required: true, value: a.launch_target });
    let pickedIcon = null;                    // иконка выбранной на ПК программы (base64 PNG), только для показа: в меню её ставит сервер
    const pickBtn = h('button', { class: 'btn', type: 'button', title: 'Выбрать из программ, найденных на ПК клуба', onclick: () => pickProgram({
      offset: ctx.club.utc_offset,
      onPick: (program) => {
        target.value = program.path; pickedIcon = program.icon || null;
        if (!title.value.trim()) title.value = program.name;
        error.textContent = '';
        sync();
      },
    }) }, 'Выбрать на ПК');
    target.addEventListener('input', () => { pickedIcon = null; sync(); });   // путь поправили вручную: иконка от прежней программы уже не подходит
    const targetLabel = h('span', {});
    const targetHint = h('div', { class: 'small muted' });
    const iconNote = h('div', { class: 'icon-note small muted' });
    const order = h('input', { type: 'number', min: 0, max: 10000, step: 1, inputMode: 'numeric', value: a.sort_order });
    const active = h('input', { type: 'checkbox', checked: a.active });
    const error = h('div', { class: 'error-text', role: 'alert' });

    function sync() {
      const steam = kind.value === 'steam';
      if (steam) pickedIcon = null;
      if (pickedIcon && tab.value === 'apps') {
        setChildren(iconNote, h('span', { class: 'program-icon' }, h('img', { src: `data:image/png;base64,${pickedIcon}`, alt: '' })), 'Иконка программы подтянется в меню сама');
      } else if (pickedIcon) {
        setChildren(iconNote, 'У игры в меню нужен вертикальный постер, а не иконка: обложку добавьте после сохранения.');
      } else setChildren(iconNote);
      targetLabel.textContent = steam ? 'Номер игры в Steam' : 'Полный путь к exe';
      target.placeholder = steam ? '570' : 'C:\\Games\\Game\\game.exe';
      pickBtn.hidden = steam;
      targetHint.textContent = steam
        ? 'Число из адреса игры в магазине Steam: store.steampowered.com/app/570 → 570. Steam на ПК должен быть установлен.'
        : 'Нажмите «Выбрать на ПК» или вставьте путь вручную: путь к файлу программы (оканчивается на .exe), а не к папке. Одинаковый на всех ПК клуба; ставьте программы для всех пользователей Windows (Program Files), а не в профиль одного.';
    }
    kind.addEventListener('change', sync);
    tab.addEventListener('change', sync);
    sync();

    async function save() {
      error.textContent = '';
      const body = {
        title: title.value.trim(), tab: tab.value, category: category.value.trim(),
        age_rating: age.value === '' ? null : Number(age.value),
        launch_kind: kind.value, launch_target: target.value.trim(),
        sort_order: Number(order.value), active: active.checked,
      };
      if (!body.title) { error.textContent = 'Введите название'; return; }
      if (!body.launch_target) { error.textContent = 'Укажите, что запускать'; return; }
      try {
        if (existing) await patch(`/api/admin/apps/${existing.id}`, body); else await post('/api/admin/apps', body);   // иконку сервер ставит сам по пути к exe
        modal.close();
        toast(existing ? 'Сохранено' : `«${body.title}» добавлена в меню`, 'ok');
        load();
      } catch (e) { error.textContent = e.message; }
    }

    const modal = openModal({
      title: existing ? 'Изменить запись' : 'Новая игра',
      content: h('form', { onsubmit: (ev) => { ev.preventDefault(); save(); } },
        field('Название', title),
        h('div', { class: 'row grow' }, field('Вкладка', tab), h('label', { class: 'field' }, h('span', {}, 'Категория'), category, catList)),
        h('div', { class: 'row grow' }, field('Возраст', age), field('Порядок в меню', order, 'Меньше число, раньше в списке')),
        field('Как запускать', kind),
        h('label', { class: 'field' }, targetLabel, h('div', { class: 'input-with-button' }, target, pickBtn), targetHint, iconNote),
        h('label', { class: 'check' }, active, h('span', {}, 'Показывать в меню')),
        error),
      actions: [h('button', { class: 'btn', type: 'button', onclick: () => modal.close() }, 'Отмена'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, existing ? 'Сохранить' : 'Добавить')],
    });
  }

  load();
}

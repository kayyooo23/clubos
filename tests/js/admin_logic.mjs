// Проверки логики админки без браузера: маска телефона и «h()» на полях, у которых свойство только для чтения (input.list).
// Запускается из tests/test_admin_js.py через Node.
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const web = path.resolve(import.meta.dirname, '..', '..', 'app', 'web', 'admin');

// минимальный document: хватает, чтобы построить элемент через h()
class FakeInput {
  constructor() { this.attrs = {}; this.children = []; this.className = ''; this.listeners = {}; this.value = ''; }
  get list() { return null; }                                  // как у настоящего <input>: только чтение
  setAttribute(k, v) { this.attrs[k] = v; }
  addEventListener(name, fn) { this.listeners[name] = fn; }
  append(...nodes) { this.children.push(...nodes); }
}
globalThis.document = { createElement: () => new FakeInput(), createTextNode: (t) => ({ text: String(t) }) };

const { phoneDigits, phoneMask } = await import(pathToFileURL(path.join(web, 'format.js')));
const { h, isWritable } = await import(pathToFileURL(path.join(web, 'dom.js')));

// ---- маска телефона ----
for (const [typed, expected] of [
  ['9001234567', '+7 (900) 123-45-67'], ['89001234567', '+7 (900) 123-45-67'], ['79001234567', '+7 (900) 123-45-67'],
  ['+7 900 123-45-67', '+7 (900) 123-45-67'], ['+7 (900) 123-45-67', '+7 (900) 123-45-67'], ['8 (900) 123-45-67', '+7 (900) 123-45-67'],
  ['900123456789999', '+7 (900) 123-45-67'], ['', ''], ['9', '+7 (9'], ['900', '+7 (900'], ['9001', '+7 (900) 1'], ['9001234', '+7 (900) 123-4'],
  ['900123456', '+7 (900) 123-45-6'], ['abc9d0e0', '+7 (900'], ['+7 (', ''], ['+7 (9', '+7 (9'],
]) assert.equal(phoneMask(phoneDigits(typed)), expected, `ввод «${typed}»`);

// ---- свойства только для чтения ----
assert.equal(isWritable(new FakeInput(), 'list'), false);
assert.equal(isWritable(new FakeInput(), 'value'), true);
const el = h('input', { list: 'categories', class: 'x', placeholder: 'p' });          // раньше падало: «Cannot set property list ... which has only a getter»
assert.equal(el.attrs.list, 'categories');
assert.equal(el.className, 'x');
console.log('ok');

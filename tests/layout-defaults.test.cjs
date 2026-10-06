const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');
const app = readFileSync('src/StreamSidekick/wwwroot/app.js', 'utf8');
const markup = readFileSync('src/StreamSidekick/wwwroot/index.html', 'utf8');
const defaults = readFileSync('src/StreamSidekick/wwwroot/layout-defaults.js', 'utf8');
const panelsInMarkup = [...markup.matchAll(/<section\b[^>]*data-panel-id="([^"]+)"[^>]*data-pages="([^"]+)"/g)];

function setup() {
  const storage = new Map();
  const context = vm.createContext({
    console,
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) }
  });
  vm.runInContext(defaults, context);
  return { context, storage, layout: context.HandlerPanelLayout };
}
function panel(id) {
  const properties = new Map();
  return {
    dataset: { panelId: id }, properties,
    style: { setProperty: (key, value) => properties.set(key, value), removeProperty: key => properties.delete(key) },
    removeAttribute: name => {},
    querySelector: () => ({ querySelector: () => ({ textContent: id }) })
  };
}

test('Every panel has a default position and valid size, including deferred panels', () => {
  const { layout } = setup();
  const ids = panelsInMarkup.map(match => match[1]);
  assert.deepEqual([...layout.order].sort(), ids.sort());
  assert.equal(new Set(layout.order).size, layout.order.length);
  for (const id of ids) for (const page of ['pre', 'during', 'after']) {
    const size = layout.sizeFor(id, page);
    assert(size.span >= 3 && size.span <= 12);
    assert(size.height === null || (size.height >= 180 && size.height <= 2400));
  }
});

test('Stage defaults lead with setup, OBS controls, and the report', () => {
  const { layout } = setup();
  for (const [page, first] of [['pre', 'optimizer'], ['during', 'scenes'], ['after', 'report']]) {
    const visible = new Set(panelsInMarkup.filter(match => match[2].split(' ').includes(page)).map(match => match[1]));
    assert.equal(layout.order.find(id => visible.has(id)), first);
  }
  for (const [left, right] of [['scenes', 'audio'], ['chat', 'alerts'], ['clips', 'ads'], ['spotify', 'tangia']]) {
    const a = layout.sizeFor(left, 'during'), b = layout.sizeFor(right, 'during');
    assert.equal(a.span + b.span, 12);
    assert.equal(a.height, b.height);
  }
  assert.equal(layout.sizeFor('clips', 'after').span, 12);
});

test('Applying defaults never replaces or saves deliberate user dimensions', () => {
  const { context, storage, layout } = setup();
  const custom = panel('chat'), untouched = panel('audio');
  Object.assign(custom.dataset, { panelSpan: '4', panelHeight: '710' });
  custom.style.setProperty('--panel-span', '4');
  custom.style.setProperty('--panel-height', '710px');
  layout.applySize(custom, 'during');
  layout.applySize(untouched, 'during');
  layout.applySize(custom, 'after');
  assert.equal(custom.properties.get('--panel-span'), '4');
  assert.equal(custom.properties.get('--panel-height'), '710px');
  assert.equal(untouched.dataset.panelSpan, undefined);
  context.$$ = () => [custom, untouched];
  context.panelSizesStorageKey = 'sizes';
  vm.runInContext(app.slice(app.indexOf('function savePanelSizes()'), app.indexOf('function readCollapsedPanels()')), context);
  context.savePanelSizes();
  assert.deepEqual(JSON.parse(storage.get('sizes')), { chat: { span: 4, height: 710 } });
});

test('Reset clears only layout overrides and retains private settings and appearance', async () => {
  const { context, storage, layout } = setup();
  const card = panel('chat');
  Object.assign(card.dataset, { panelSpan: '4', panelHeight: '710' });
  card.removeAttribute = name => delete card.dataset[name === 'data-panel-span' ? 'panelSpan' : 'panelHeight'];
  const keys = ['collapsed', 'sizes', 'floating', 'tabs'];
  for (const key of [...keys, 'theme', 'simpleChoices', 'integrationPreferences']) storage.set(key, 'unchanged');
  let order, pages, selectedPage;
  Object.assign(context, {
    useRecommendedPanelLayout: false, defaultLayoutStorageKey: 'defaults', defaultPanelOrder: layout.order,
    collapsedPanelsStorageKey: keys[0], panelSizesStorageKey: keys[1], floatingPanelsStorageKey: keys[2], panelTabGroupsStorageKey: keys[3],
    panelTabGroups: [{ panels: ['chat', 'audio'] }], $$: () => [card],
    document: { body: { dataset: { workspacePage: 'during' } } },
    applyPanelOrder: value => order = [...value], applyPanelPages: value => pages = value,
    setPanelCollapsed: (target, value) => target.collapsed = value,
    setPanelFloating: (target, value) => target.floating = value,
    setActiveWorkspace: page => { selectedPage = page; layout.applySize(card, page); },
    savePanelOrder: async () => true
  });
  vm.runInContext(app.slice(app.indexOf('function restoreDefaultPanelLayout()'), app.indexOf('function nearestPanelInDirection(')), context);
  assert.equal(await context.restoreDefaultPanelLayout(), true);
  assert.deepEqual(order, [...layout.order]);
  assert.equal(Object.keys(pages).length, 0);
  assert.equal(selectedPage, 'during');
  assert.equal(card.collapsed, false);
  assert.equal(card.floating, false);
  assert.equal(card.dataset.panelSpan, undefined);
  assert.equal(context.panelTabGroups.length, 0);
  keys.forEach(key => assert.equal(storage.has(key), false));
  ['theme', 'simpleChoices', 'integrationPreferences'].forEach(key => assert.equal(storage.get(key), 'unchanged'));
  assert.equal(storage.get('defaults'), 'true');
});

test('Reset asks first, and the defaults script loads before dashboard initialization', () => {
  assert(markup.indexOf('src="layout-defaults.js') < markup.indexOf('src="app.js'));
  assert.match(app, /\$\("#reset-layout"\)\.addEventListener\("click", \(\) => \$\("#reset-layout-dialog"\)\.showModal\(\)\)/);
  assert.match(markup, /value="cancel">Keep my layout/);
});

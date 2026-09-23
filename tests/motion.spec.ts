import { test, expect, type Page } from '@playwright/test';
import { initialState } from '../src/data';

async function loadDock(page: Page, reducedMotion = false, width = 640, motionSpeed?: 'relaxed' | 'standard' | 'brisk') {
  const initial = structuredClone(initialState);
  initial.preferences.reducedMotion = reducedMotion;
  initial.preferences.width = width;
  if (motionSpeed) (initial.preferences as any).motionSpeed = motionSpeed;
  if (width === 320) initial.projects = initial.projects.slice(0, 1);
  await page.addInitScript(({ initial }) => {
    const listeners: Array<(event: { data: unknown }) => void> = [];
    let visibilityId = 0;
    const host = window as any;
    host.__motionCalls = [];
    host.__motionShow = (visible: boolean) => listeners.forEach(listener => listener({ data: {
      protocol: 1, type: 'event', event: 'window.visibility', data: { visible, visibilityId: ++visibilityId },
    } }));
    host.chrome = host.chrome || {};
    host.chrome.webview = {
      addEventListener: (_: string, listener: typeof listeners[number]) => listeners.push(listener),
      postMessage: (req: any) => {
        host.__motionCalls.push(req);
        // Hold a fresh expanded layout until the test explicitly acknowledges it.
        const respond = () => listeners.forEach(listener => listener({ data: { protocol: 1,
          type: 'response', id: req.id, ok: true,
          result: req.method === 'app.getState' ? initial : { applied: true },
        } }));
        if (req.method === 'window.sync' && req.params.expanded && host.__holdMotionLayout) host.__motionAcks.push(respond);
        else setTimeout(respond, 0);
      },
    };
    host.__motionAcks = [];
  }, { initial });
  await page.goto('/?view=dock&mode=native');
  await expect(page.locator('.dock-handle')).toBeAttached();
}

test('native exit is not reversed by renderer pointer movement or focus changes from other windows', async ({ page }) => {
  await loadDock(page);
  await page.evaluate(() => (window as any).__motionShow(true));
  const wrap = page.locator('.dock-wrap');
  await expect(wrap).toBeVisible();
  await expect.poll(() => wrap.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await page.evaluate(() => (window as any).__motionShow(false));
  await expect(wrap).toHaveClass(/dock-closing/);
  await wrap.evaluate(el => {
    el.getAnimations()[0].pause();
    window.dispatchEvent(new Event('blur'));
    el.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, movementX: 1, movementY: 1 }));
    window.dispatchEvent(new Event('focus'));
  });
  await expect(wrap).toHaveClass(/dock-closing/);
  await wrap.evaluate(el => el.getAnimations()[0].finish());
  await expect(wrap).toHaveCount(0);
  expect(await page.evaluate(() => (window as any).__motionCalls.filter((c: any) => c.method === 'window.sync').at(-1).params.expanded)).toBe(false);
});

test('entrance starts above the edge and waits for expanded layout before a continuous slide', async ({ page }) => {
  await loadDock(page);
  await page.evaluate(() => { (window as any).__holdMotionLayout = true; (window as any).__motionShow(true); });
  const wrap = page.locator('.dock-wrap');
  await expect(wrap).toBeAttached();
  const pending = await wrap.evaluate(el => {
    const a = el.getAnimations()[0];
    return { bottom: el.getBoundingClientRect().bottom, state: a.playState, time: Number(a.currentTime) };
  });
  expect(pending.bottom).toBeLessThan(0);
  expect(pending.state).toBe('paused');
  expect(pending.time).toBe(0);
  await page.evaluate(() => { (window as any).__holdMotionLayout = false; (window as any).__motionAcks.splice(0).forEach((ack: () => void) => ack()); });
  const samples = await wrap.evaluate(el => {
    const a = el.getAnimations()[0]; a.pause();
    const duration = Number(a.effect!.getTiming().duration);
    return { duration, frames: [0, .2, .4, .6, .8, 1].map(part => {
      a.currentTime = duration * part;
      const m = new DOMMatrixReadOnly(getComputedStyle(el).transform);
      return { y: m.m42, scaleX: m.m11, scaleY: m.m22, opacity: Number(getComputedStyle(el).opacity) };
    }) };
  });
  expect(samples.duration).toBe(520);
  samples.frames.forEach((frame, i) => {
    expect(frame.scaleX).toBe(1); expect(frame.scaleY).toBe(1);
    expect(frame.opacity).toBe(1);
    expect(frame.y).toBeLessThanOrEqual(0);
    if (i) expect(frame.y).toBeGreaterThan(samples.frames[i - 1].y);
  });
  expect(samples.frames.at(-1)!.y).toBe(0);
  // Symmetric slow start/end keeps the travel distributed across the duration.
  expect(samples.frames[1].y + samples.frames[4].y).toBeCloseTo(samples.frames[0].y, 3);
});

test('exit remains mounted, reverses at the current frame, and does not sync every animation frame', async ({ page }) => {
  await loadDock(page);
  await page.evaluate(() => (window as any).__motionShow(true));
  const wrap = page.locator('.dock-wrap');
  await expect(wrap).toBeVisible();
  await expect.poll(() => wrap.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await page.evaluate(() => { (window as any).__motionCalls = []; (window as any).__motionShow(false); });
  await expect(wrap).toHaveClass(/dock-closing/);
  const before = await wrap.evaluate(el => {
    const a = el.getAnimations()[0]; a.pause(); a.currentTime = 300;
    const bottomAt300 = el.getBoundingClientRect().bottom;
    a.currentTime = 120;
    const style = getComputedStyle(el);
    return { transform: style.transform, opacity: style.opacity, duration: a.effect!.getTiming().duration, bottomAt300 };
  });
  expect(before.duration).toBe(450);
  expect(Number(before.opacity)).toBe(1);
  expect(before.bottomAt300).toBeGreaterThan(0);
  await page.evaluate(() => (window as any).__motionShow(true));
  await expect(wrap).not.toHaveClass(/dock-closing/);
  const first = await wrap.evaluate(el => (el.getAnimations()[0].effect as KeyframeEffect).getKeyframes()[0]);
  expect(first.transform).toBe(before.transform);
  expect(Number(first.opacity)).toBeCloseTo(Number(before.opacity), 4);
  await expect.poll(() => wrap.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await page.evaluate(() => { (window as any).__motionCalls = []; (window as any).__motionShow(false); });
  await expect(wrap).toHaveCount(0);
  const calls = await page.evaluate(() => (window as any).__motionCalls.filter((call: any) => call.method === 'window.sync'));
  expect(calls.length).toBeLessThanOrEqual(4);
  expect(calls.at(-1).params.expanded).toBe(false);
});

test('reduced motion exits immediately without retaining an invisible dock', async ({ page }) => {
  await loadDock(page, true);
  await page.evaluate(() => (window as any).__motionShow(true));
  await expect(page.locator('.dock-wrap')).toBeVisible();
  await page.evaluate(() => (window as any).__motionShow(false));
  await expect(page.locator('.dock-wrap')).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => (window as any).__motionCalls.filter((call: any) => call.method === 'window.sync').at(-1).params.expanded)).toBe(false);
});

test('explicit software animation remains enabled when Windows requests reduced motion', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await loadDock(page, false);
  await page.evaluate(() => { (window as any).__holdMotionLayout = true; (window as any).__motionShow(true); });
  const wrap = page.locator('.dock-wrap');
  await expect(wrap).toBeAttached();
  const animation = await wrap.evaluate(el => ({
    reducedMedia: matchMedia('(prefers-reduced-motion: reduce)').matches,
    duration: el.getAnimations()[0].effect!.getTiming().duration,
    bottom: el.getBoundingClientRect().bottom,
    state: el.getAnimations()[0].playState,
  }));
  expect(animation.reducedMedia).toBe(true);
  expect(animation.duration).toBe(520);
  expect(animation.bottom).toBeLessThan(0);
  expect(animation.state).toBe('paused');
});

test('a stack wider than the dock keeps its whole painted width inside the native exit region', async ({ page }) => {
  await loadDock(page, false, 320);
  await page.evaluate(() => (window as any).__motionShow(true));
  const wrap = page.locator('.dock-wrap');
  await expect(wrap).toBeVisible();
  await expect.poll(() => wrap.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await page.getByRole('button', { name: '打开 品牌设计 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  await expect(page.locator('.stack-panel')).toBeVisible();
  await page.mouse.move(20, 650);
  await page.evaluate(() => (window as any).__motionShow(false));
  await expect(wrap).toHaveClass(/dock-closing/);
  const coverageAt = (time: number) => wrap.evaluate((el, time) => {
    const a = el.getAnimations()[0]; a.pause(); a.currentTime = time;
    const stack = el.parentElement!.querySelector('.stack-panel')!.getBoundingClientRect();
    const dock = el.querySelector('.dock')!.getBoundingClientRect();
    const rects = (window as any).__motionCalls.filter((call: any) => call.method === 'window.sync').at(-1).params.rects;
    // These points are in the wide stack's painted shadow, above its resting
    // position. Even the bounding boxes of native rounded ShadowBounds must
    // contain them; a stationary-only region cannot cover this trajectory.
    const points = [stack.left - 20, stack.right + 20].map(x => ({ x, y: 20 }));
    return { wider: stack.width > dock.width, covers: points.map(point => rects.some((r: any) =>
      point.x >= r.x - 28 && point.x <= r.x + r.width + 28 &&
      point.y >= r.y - 28 && point.y <= r.y + r.height + 36)),
    stackBottom: stack.bottom };
  }, time);
  const coverage = await coverageAt(120);
  expect(coverage.wider).toBe(true);
  expect(coverage.stackBottom).toBeGreaterThan(20);
  expect(coverage.covers).toEqual([true, true]);
  await page.evaluate(() => (window as any).__motionShow(true));
  await expect(wrap).not.toHaveClass(/dock-closing/);
  expect((await coverageAt(0)).covers).toEqual([true, true]);
});

test('bar retracts completely before the faster stack follows', async ({ page }) => {
  await loadDock(page, false, 640, 'relaxed');
  await page.evaluate(() => (window as any).__motionShow(true));
  const wrap = page.locator('.dock-wrap');
  await expect(wrap).toBeVisible();
  await expect.poll(() => wrap.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await page.getByRole('button', { name: '打开 品牌设计 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  const panel = page.locator('.stack-panel');
  await expect(panel).toBeVisible();
  const enterTiming = await panel.evaluate(el => { const a = el.getAnimations().at(-1)!; return { duration: Number(a.effect!.getTiming().duration), delay: Number(a.effect!.getTiming().delay) }; });
  expect(enterTiming.duration).toBe(200);
  await expect.poll(() => panel.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  const resting = await panel.boundingBox();
  await page.evaluate(() => (window as any).__motionShow(false));
  // The wrap retracts immediately; the open panel plays its own faster upward slide behind it.
  await expect(wrap).toHaveClass(/dock-closing/);
  await expect(panel).toHaveClass(/stack-leaving/);
  const exitTiming = await panel.evaluate(el => { const a = el.getAnimations().at(-1)!; return { duration: Number(a.effect!.getTiming().duration), delay: Number(a.effect!.getTiming().delay) }; });
  expect(exitTiming).toEqual({ duration: 200, delay: 600 });
  const early = await panel.boundingBox();
  expect(early!.y).toBeCloseTo(resting!.y, 0);
  expect(await wrap.evaluate(el => Number(el.getAnimations()[0].effect!.getTiming().duration))).toBe(600);
  await expect(panel).toHaveCount(0);
});

test('submenu exit completion never shrinks its native reentry region', async ({ page }) => {
  await loadDock(page, false, 640, 'relaxed');
  await page.evaluate(() => (window as any).__motionShow(true));
  await expect.poll(() => page.locator('.dock-wrap').evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await page.getByRole('button', { name: '打开 品牌设计 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  const panel = page.locator('.stack-panel');
  await expect.poll(() => panel.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  const resting = await panel.boundingBox();
  await page.evaluate(() => (window as any).__motionShow(false));
  await page.locator('.dock-wrap').evaluate(el => el.getAnimations()[0].pause());
  await panel.evaluate(el => {
    el.getAnimations().forEach(a => a.finish());
    el.dispatchEvent(new AnimationEvent('animationend', { bubbles: true }));
  });
  const rects = await page.evaluate(() => (window as any).__motionCalls.filter((c: any) => c.method === 'window.sync').at(-1).params.rects);
  expect(rects.some((r: any) => resting!.x + 10 >= r.x && resting!.x + 10 <= r.x + r.width && resting!.y + resting!.height - 10 >= r.y && resting!.y + resting!.height - 10 <= r.y + r.height)).toBe(true);
  await page.evaluate(() => (window as any).__motionShow(true));
  await expect(page.locator('.dock-wrap')).not.toHaveClass(/dock-closing/);
  await expect.poll(() => panel.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await expect(panel).toBeVisible();
});

test('a returning submenu reverses from its current frame and waits for the bar on reveal', async ({ page }) => {
  await loadDock(page);
  await page.evaluate(() => (window as any).__motionShow(true));
  const wrap = page.locator('.dock-wrap'), panel = page.locator('.stack-panel');
  await expect.poll(() => wrap.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await page.getByRole('button', { name: '打开 品牌设计 主目录，长按展开堆叠', exact: true }).press('ArrowDown');
  await expect.poll(() => panel.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await page.evaluate(() => (window as any).__motionShow(false));
  await wrap.evaluate(el => el.getAnimations()[0].pause());
  const before = await panel.evaluate(el => {
    const a = el.getAnimations()[0]; a.pause();
    a.currentTime = Number(a.effect!.getTiming().delay) + Number(a.effect!.getTiming().duration) / 2;
    return getComputedStyle(el).transform;
  });
  await page.evaluate(() => (window as any).__motionShow(true));
  const after = await panel.evaluate(el => {
    const a = el.getAnimations()[0]; a.pause(); a.currentTime = 0;
    return { transform: getComputedStyle(el).transform, delay: a.effect!.getTiming().delay };
  });
  expect(after.transform).toBe(before);
  expect(after.delay).toBe(520);
  await panel.evaluate(el => el.getAnimations()[0].play());
  await expect.poll(() => panel.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await page.evaluate(() => (window as any).__motionShow(false));
  await expect(wrap).toHaveCount(0);
  await page.evaluate(() => (window as any).__motionShow(true));
  await expect(panel).toBeAttached();
  expect(await panel.evaluate(el => el.getAnimations()[0].effect!.getTiming().delay)).toBe(520);
  await expect.poll(() => panel.evaluate(el => el.getAnimations().every(a => a.playState === 'finished'))).toBe(true);
  await expect(panel).toBeVisible();
});

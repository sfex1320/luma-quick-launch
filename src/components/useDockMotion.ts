import { useCallback, useLayoutEffect, useRef, useState } from 'react';

interface DockMotionOptions {
  visible: boolean;
  reducedMotion: boolean;
  /** A native reveal waits for its expanded window.sync acknowledgement. */
  waitForLayout?: boolean;
  /** Enter/exit durations in ms; the user preference picks one of three gears. */
  durations?: { enter: number; exit: number; stack?: number };
  menuKey?: string;
  onExited: () => void;
}

/** Recover stationary layout coordinates without reporting every animated frame. */
export function getDockTranslationY(element: HTMLElement | null): number {
  if (!element) return 0;
  const transform = getComputedStyle(element).transform;
  return transform === 'none' ? 0 : new DOMMatrixReadOnly(transform).m42;
}

function hiddenTranslation(element: HTMLElement): number {
  const currentY = getDockTranslationY(element);
  const bottom = element.getBoundingClientRect().bottom;
  return -Math.max(16, bottom - currentY + 16);
}

/** Independent compositor animations; presence ends after both current exits. */
export function useDockMotion({ visible, reducedMotion, waitForLayout = false, durations = { enter: 650, exit: 600 }, menuKey, onExited }: DockMotionOptions) {
  const [present, setPresent] = useState(visible);
  const wrap = useRef<HTMLDivElement>(null);
  const mountedElement = useRef<HTMLElement | null>(null);
  const motion = useRef<{ animation: Animation; entering: boolean } | null>(null);
  const panels = useRef(new Map<HTMLElement, Animation>());
  const panelState = useRef({ visible, menuKey });
  const entranceFrame = useRef<number | null>(null);
  const exitCallback = useRef(onExited);
  exitCallback.current = onExited;

  const resumeEntrance = useCallback(() => {
    const current = motion.current;
    if (!current?.entering || current.animation.playState !== 'paused' || entranceFrame.current !== null) return;
    // The acknowledgement means HWND/region are ready. Keep the first hidden
    // transform through a paint before advancing the compositor animation.
    entranceFrame.current = requestAnimationFrame(() => {
      entranceFrame.current = requestAnimationFrame(() => {
        entranceFrame.current = null;
        if (motion.current === current && current.animation.playState === 'paused') {
          current.animation.play();
          for (const animation of panels.current.values()) if (animation.playState === 'paused') animation.play();
        }
      });
    });
  }, []);
  const getTranslationY = useCallback((element?: HTMLElement) => {
    if (element?.matches('.stack-panel,.more-panel')) return getDockTranslationY(element);
    return !element || wrap.current?.contains(element) ? getDockTranslationY(wrap.current) : 0;
  }, []);

  const finishExit = useCallback(() => {
    if (!wrap.current) return;
    if (motion.current?.entering || motion.current?.animation.playState !== 'finished') return;
    if ([...panels.current.values()].some(animation => animation.playState !== 'finished')) return;
    setPresent(false); exitCallback.current();
  }, []);

  useLayoutEffect(() => {
    if (visible && !present) { setPresent(true); return; }
    const element = wrap.current;
    if (!element) { mountedElement.current = null; return; }

    const firstFrame = mountedElement.current !== element || !motion.current;
    const hiddenY = hiddenTranslation(element);
    const style = getComputedStyle(element);
    // Read before cancelling: reversal starts at the exact displayed frame.
    const from = firstFrame
      ? { opacity: '1', transform: `translateY(${hiddenY}px)` }
      : { opacity: style.opacity, transform: style.transform };
    const target = visible
      ? { opacity: '1', transform: 'translateY(0px)' }
      : { opacity: '1', transform: `translateY(${hiddenY}px)` };
    if (entranceFrame.current !== null) cancelAnimationFrame(entranceFrame.current);
    entranceFrame.current = null;
    motion.current?.animation.cancel();
    mountedElement.current = element;
    element.style.opacity = from.opacity;
    element.style.transform = from.transform;

    // Luma's explicit preference controls this animation: the user requested
    // full sliding motion even when Windows reduces its own animations.
    const reduced = reducedMotion;
    element.style.willChange = reduced ? '' : 'transform';
    const animation = element.animate([from, target], {
      duration: reduced ? 0 : visible ? durations.enter : durations.exit,
      delay: !visible && !reduced && element.parentElement?.querySelector('.stack-panel,.more-panel') ? durations.stack ?? 160 : 0,
      easing: 'cubic-bezier(.42,0,.58,1)',
      fill: 'forwards',
    });
    motion.current = { animation, entering: visible };
    if (visible && firstFrame && waitForLayout && !reduced) {
      animation.pause();
      animation.currentTime = 0;
    }
    animation.onfinish = () => {
      if (motion.current?.animation !== animation) return;
      element.style.opacity = target.opacity;
      element.style.transform = target.transform;
      element.style.willChange = '';
      if (!visible) finishExit();
    };
    // Do not cancel in dependency cleanup: the next effect reads this frame.
    return () => { animation.onfinish = null; };
  }, [visible, present, reducedMotion, waitForLayout, durations, finishExit]);

  useLayoutEffect(() => {
    const elements = [...wrap.current?.parentElement?.querySelectorAll<HTMLElement>('.stack-panel,.more-panel') ?? []];
    const previous = panels.current;
    const switching = visible && panelState.current.visible && panelState.current.menuKey !== menuKey && previous.size > 0;
    panelState.current = { visible, menuKey };
    panels.current = new Map();
    for (const element of elements) {
      const old = previous.get(element);
      const from = switching ? '0px' : old ? `${getDockTranslationY(element)}px` : 'calc(-100% - 16px)';
      old?.cancel();
      const target = visible ? '0px' : 'calc(-100% - 16px)';
      const primary = motion.current?.animation;
      const remaining = primary && primary.playState !== 'finished'
        ? Math.max(0, Number(primary.effect!.getComputedTiming().endTime) - Number(primary.currentTime ?? 0)) : 0;
      const animation = element.animate([
        { transform: `translate(-50%,${from})` },
        { transform: `translate(-50%,${target})` },
      ], { duration: reducedMotion || switching ? 0 : durations.stack ?? 160, delay: reducedMotion || switching || !visible ? 0 : remaining,
        easing: 'cubic-bezier(.42,0,.58,1)', fill: 'both' });
      if (primary?.playState === 'paused') { animation.pause(); animation.currentTime = 0; }
      panels.current.set(element, animation);
      animation.onfinish = () => { if (panels.current.get(element) === animation && !visible) finishExit(); };
    }
    for (const [element, animation] of previous) if (!panels.current.has(element)) animation.cancel();
    if (!visible) finishExit();
    return () => { for (const animation of panels.current.values()) animation.onfinish = null; };
  }, [visible, present, menuKey, reducedMotion, durations, finishExit]);

  useLayoutEffect(() => () => {
    if (entranceFrame.current !== null) cancelAnimationFrame(entranceFrame.current);
    entranceFrame.current = null;
    motion.current?.animation.cancel();
    motion.current = null;
    for (const animation of panels.current.values()) animation.cancel();
    panels.current.clear();
  }, []);

  return { present, wrap, getTranslationY, resumeEntrance };
}

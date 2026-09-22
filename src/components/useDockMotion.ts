import { useCallback, useLayoutEffect, useRef, useState } from 'react';

interface DockMotionOptions {
  visible: boolean;
  reducedMotion: boolean;
  /** A native reveal waits for its expanded window.sync acknowledgement. */
  waitForLayout?: boolean;
  /** Enter/exit durations in ms; the user preference picks one of three gears. */
  durations?: { enter: number; exit: number };
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
  // Absolutely positioned directory/overflow panels must clear the edge as well.
  let bottom = element.getBoundingClientRect().bottom;
  for (const child of element.querySelectorAll<HTMLElement>('[data-native-hit]')) {
    bottom = Math.max(bottom, child.getBoundingClientRect().bottom);
  }
  return -Math.max(16, bottom - currentY + 16);
}

/** A single compositor animation; presence ends only after the current exit. */
export function useDockMotion({ visible, reducedMotion, waitForLayout = false, durations = { enter: 650, exit: 600 }, onExited }: DockMotionOptions) {
  const [present, setPresent] = useState(visible);
  const wrap = useRef<HTMLDivElement>(null);
  const mountedElement = useRef<HTMLElement | null>(null);
  const motion = useRef<{ animation: Animation; entering: boolean } | null>(null);
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
        if (motion.current === current && current.animation.playState === 'paused') current.animation.play();
      });
    });
  }, []);
  const getTranslationY = useCallback(() => getDockTranslationY(wrap.current), []);

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
      if (!visible) { setPresent(false); exitCallback.current(); }
    };
    // Do not cancel in dependency cleanup: the next effect reads this frame.
    return () => { animation.onfinish = null; };
  }, [visible, present, reducedMotion, waitForLayout, durations]);

  useLayoutEffect(() => () => {
    if (entranceFrame.current !== null) cancelAnimationFrame(entranceFrame.current);
    entranceFrame.current = null;
    motion.current?.animation.cancel();
    motion.current = null;
  }, []);

  return { present, wrap, getTranslationY, resumeEntrance };
}

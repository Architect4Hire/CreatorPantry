import { LandingCount } from './landing-count';

/** A window stub: `matches` decides reduced motion, and frames are run by hand. */
function fakeView(reducedMotion: boolean) {
  const frames: FrameRequestCallback[] = [];
  let now = 0;
  const view = {
    matchMedia: () => ({ matches: reducedMotion }) as MediaQueryList,
    requestAnimationFrame: (callback: FrameRequestCallback) => frames.push(callback),
    performance: { now: () => now },
  } as unknown as Window;

  return {
    view,
    frames,
    tick: (ms: number) => (now += ms),
    run: () => frames.splice(0).forEach((frame) => frame(now)),
  };
}

describe('LandingCount', () => {
  const from = new Map([['a', 0]]);
  const to = new Map([['a', 100]]);

  it('answers the real figure while nothing is counting', () => {
    const count = new LandingCount(fakeView(false).view);

    expect(count.shownOr('a', 42)).toBe(42);
  });

  /** Without this the figure appears at its destination for one paint, then counts up to it from the start. */
  it('shows where it is counting from before the first frame runs', () => {
    const { view } = fakeView(false);
    const count = new LandingCount(view);

    count.run(from, to);

    expect(count.shownOr('a', 100)).toBe(0);
  });

  it('counts to the real figure and then gets out of the way', () => {
    const stub = fakeView(false);
    const count = new LandingCount(stub.view);

    count.run(from, to);
    stub.tick(175);
    stub.run();
    const halfway = count.shownOr('a', 100);

    stub.tick(400);
    stub.run();

    expect(halfway).toBeGreaterThan(0);
    expect(halfway).toBeLessThan(100);
    expect(count.shownOr('a', 100)).toBe(100);
  });

  /** Two counts at once drew to different targets, and whichever ended first cleared the other's figures. */
  it('abandons a count when another starts', () => {
    const stub = fakeView(false);
    const count = new LandingCount(stub.view);

    count.run(from, to);
    stub.tick(100);
    count.run(new Map([['a', 50]]), new Map([['a', 60]]));
    stub.tick(100);
    stub.run();

    const shown = count.shownOr('a', 60);
    expect(shown).toBeGreaterThanOrEqual(50);
    expect(shown).toBeLessThanOrEqual(60);
  });

  it('does not count at all with motion turned down', () => {
    const stub = fakeView(true);
    const count = new LandingCount(stub.view);

    count.run(from, to);

    expect(stub.frames.length).toBe(0);
    expect(count.shownOr('a', 100)).toBe(100);
  });

  it('stops when its owner is destroyed', () => {
    const stub = fakeView(false);
    const count = new LandingCount(stub.view);

    count.run(from, to);
    count.stop();
    stub.tick(100);
    stub.run();

    expect(count.shownOr('a', 100)).toBe(100);
  });

  it('counts nothing where there is no window at all', () => {
    const count = new LandingCount(null);

    count.run(from, to);

    expect(count.shownOr('a', 100)).toBe(100);
  });
});

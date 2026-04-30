import {
  coerceHydraulicDropsPerPassCandidate,
  loadWorldDashboardUi,
} from './world-dashboard-ui.storage';

describe('WorldDashboardUi storage', () => {
  const storageKey = 'wb.worldDashboard.settings.v1';

  afterEach(() => {
    window.localStorage.removeItem(storageKey);
  });

  /**
   * System under test: {@link coerceHydraulicDropsPerPassCandidate}.
   * Test case: off-grid values snap to nearest thousand and clamp into [20&nbsp;000, 10&nbsp;000&nbsp;000].
   * Expected result: 100&nbsp;534 → 100&nbsp;000; values below/at band edges clamp correctly.
   * Why important: Hydraulic drops agree with erosion UI rounding and server clamps.
   */
  it('coerceHydraulicDropsPerPassCandidate snaps thousands and clamps', () => {
    expect(coerceHydraulicDropsPerPassCandidate(100_534)).toBe(101_000);
    expect(coerceHydraulicDropsPerPassCandidate(19_499)).toBe(20_000);
    expect(coerceHydraulicDropsPerPassCandidate(5_000_533)).toBe(5_001_000);
    expect(coerceHydraulicDropsPerPassCandidate(11_950_500)).toBe(10_000_000);
  });

  /**
   * System under test: {@link loadWorldDashboardUi}.
   * Test case: JSON stores `v !== 1` — invalid version discarded.
   * Expected result: `null`.
   * Why important: Schema evolution must not hydrate obsolete blobs into the dashboard.
   */
  it('loadWorldDashboardUi returns null when version mismatches', () => {
    window.localStorage.setItem(storageKey, JSON.stringify({ v: 0, seed: 7 }));
    expect(loadWorldDashboardUi()).toBeNull();
  });
});

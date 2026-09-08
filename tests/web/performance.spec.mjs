import { test, expect } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { installServer, passwordLogin, track } from './fixture.mjs';

function observePerformance() {
  const metrics = { lcpMilliseconds: 0, cls: 0, worstInteractionMilliseconds: 0, observedInteractions: 0, layoutShifts: [], clicks: [] };
  let start = 0;
  let last = 0;
  let shift = 0;
  const interactions = new Set();
  globalThis.jellysinLabMetrics = metrics;
  globalThis.addEventListener('click', event => {
    if (metrics.clicks.length < 50) metrics.clicks.push({ time: globalThis.performance.now(), element: event.target.id || event.target.nodeName });
  });
  new globalThis.PerformanceObserver(list => {
    for (const entry of list.getEntries()) metrics.lcpMilliseconds = entry.startTime;
  }).observe({ type: 'largest-contentful-paint', buffered: true });
  new globalThis.PerformanceObserver(list => {
    for (const entry of list.getEntries()) {
      if (entry.hadRecentInput) continue;
      if (metrics.layoutShifts.length < 50) metrics.layoutShifts.push({
        startTime: entry.startTime, value: entry.value,
        sources: entry.sources.map(source => ({
          element: source.node?.id || source.node?.nodeName,
          previous: source.previousRect.toJSON(), current: source.currentRect.toJSON(),
        })),
      });
      if (entry.startTime - last > 1000 || entry.startTime - start > 5000) { shift = 0; start = entry.startTime; }
      last = entry.startTime;
      shift += entry.value;
      metrics.cls = Math.max(metrics.cls, shift);
    }
  }).observe({ type: 'layout-shift', buffered: true });
  new globalThis.PerformanceObserver(list => {
    for (const entry of list.getEntries()) {
      if (!entry.interactionId) continue;
      interactions.add(entry.interactionId);
      metrics.observedInteractions = interactions.size;
      metrics.worstInteractionMilliseconds = Math.max(metrics.worstInteractionMilliseconds, entry.duration);
    }
  }).observe({ type: 'event', buffered: true, durationThreshold: 16 });
}

for (const width of [320, 1280]) {
  test(`page performance budgets with 4x CPU slowdown at ${width}px`, async ({ page, context, browser }, testInfo) => {
    const protocol = await context.newCDPSession(page);
    await protocol.send('Emulation.setCPUThrottlingRate', { rate: 4 });
    await page.setViewportSize({ width, height: 900 });
    await page.addInitScript(observePerformance);
    const state = await installServer(page, { prefix: '/jellyfin', latencyMs: 80 });
    state.historyTracks = Array.from({ length: 200 }, (_, index) => ({ ...track, Title: `History track ${index}` }));
    await expect(page.locator('#quick-connect')).toBeEnabled();
    await expect.poll(() => page.evaluate(() => globalThis.jellysinLabMetrics.lcpMilliseconds)).toBeGreaterThan(0);
    await passwordLogin(page);
    for (const id of ['history-refresh', 'charts-refresh', 'discovery-refresh', 'favourites-refresh', 'playlists-refresh']) {
      await page.locator(`#${id}`).click();
      await expect(page.locator('#workspace')).toHaveAttribute('aria-busy', 'false');
    }
    await page.getByRole('button', { name: 'Sign out', exact: true }).click();
    await expect(page.locator('#sign-in')).toBeVisible();
    // Event timing entries arrive after the next presentation frame.
    await page.evaluate(() => new Promise(resolve => globalThis.requestAnimationFrame(() => globalThis.requestAnimationFrame(resolve))));
    const metrics = await page.evaluate(() => globalThis.jellysinLabMetrics);
    const report = JSON.stringify({
      ...metrics, viewportWidth: width, viewportHeight: 900, browser: browser.version(), cpuSlowdown: 4,
      fixtureLatencyMillisecondsPerResponse: 80, networkBandwidthThrottled: false, renderedHistoryTracks: 200,
      responsivenessMetric: 'Worst observed interaction; events below 16ms are not reported. This laboratory result is not field INP.',
    }, null, 2);
    const reportPath = testInfo.outputPath('frontend-lab-metrics.json');
    await writeFile(reportPath, report + '\n');
    await testInfo.attach('frontend-lab-metrics', { contentType: 'application/json', path: reportPath });
    await protocol.detach();
    expect(metrics.lcpMilliseconds).toBeLessThan(2500);
    expect(metrics.cls).toBeLessThan(0.1);
    expect(metrics.worstInteractionMilliseconds).toBeLessThan(200);
  });
}

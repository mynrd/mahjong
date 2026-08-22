import { expect, test } from '@playwright/test';
import { closeAll, createRoom, deal, fillWithBots } from './helpers';

/**
 * Runs on all three viewports (phone, tablet, desktop) and writes a screenshot of each screen for
 * each one. The assertions catch the failure that matters on a small screen and is invisible on a
 * large one: the page scrolling sideways because something does not fit.
 */
test.describe('layout across screen sizes', () => {
  test('every screen fits its viewport and is legible', async ({ browser }, testInfo) => {
    test.setTimeout(120_000);

    const size = testInfo.project.name;
    const shot = (name: string) => `screenshots/${size}-${name}.png`;

    const { host } = await createRoom(browser, { roomName: 'Lola Tables' });
    const page = host.page;

    // ---------------------------------------------------------------- create
    const fresh = await browser.newContext({ viewport: page.viewportSize() ?? undefined });
    const freshPage = await fresh.newPage();
    await freshPage.goto('/');
    await expect(freshPage.getByTestId('create-form')).toBeVisible();
    await freshPage.screenshot({ path: shot('01-create'), fullPage: true });
    await noSidewaysScroll(freshPage);
    await fresh.close();

    // ---------------------------------------------------------------- lobby
    await expect(page.getByTestId('invite-qr')).toBeVisible();
    await page.screenshot({ path: shot('02-lobby'), fullPage: true });
    await noSidewaysScroll(page);

    // ---------------------------------------------------------------- table
    await fillWithBots(host);
    await deal(host, [host]);

    await expect(page.getByTestId('my-hand').locator('.tile-button')).toHaveCount(17);
    await page.screenshot({ path: shot('03-table') });
    await noSidewaysScroll(page);

    // The hand itself scrolls sideways on a narrow screen, which is intended, but the page must
    // not. Check the tiles are a usable size while we are here: below about 28px across, the
    // artwork stops being readable at arm's length.
    const tileWidth = await page
      .getByTestId('my-hand')
      .locator('.tile-button')
      .first()
      .evaluate((node) => node.getBoundingClientRect().width);

    expect(tileWidth).toBeGreaterThanOrEqual(28);

    // ---------------------------------------------------------------- auto arrange
    // The gaps between blocks are sized off the tile rather than in fixed pixels, so a grouped
    // hand still scrolls inside its own row on a phone instead of pushing the page sideways.
    await page.getByTestId('auto-arrange').click();
    await expect(page.getByTestId('my-hand').locator('.group[data-group="all"]')).toHaveCount(0);

    await expect(page.getByTestId('my-hand').locator('.tile-button')).toHaveCount(17);
    await page.screenshot({ path: shot('04-table-arranged') });
    await noSidewaysScroll(page);

    await page.getByTestId('auto-arrange').click();

    // ---------------------------------------------------------------- action bar
    await expect(page.getByTestId('turn-bar')).toBeInViewport();

    await closeAll([host]);
  });
});

/**
 * The page must never scroll horizontally. On a phone this is the difference between a playable
 * board and one where half the table is off the side with no way to reach it.
 */
async function noSidewaysScroll(page: import('@playwright/test').Page): Promise<void> {
  const overflow = await page.evaluate(() => {
    const doc = document.documentElement;
    return { scrollWidth: doc.scrollWidth, clientWidth: doc.clientWidth };
  });

  // One pixel of slack for sub-pixel rounding on fractional device pixel ratios.
  expect(overflow.scrollWidth).toBeLessThanOrEqual(overflow.clientWidth + 1);
}

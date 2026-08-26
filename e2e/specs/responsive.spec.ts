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

    // ---------------------------------------------------------------- the action bar stays put
    // The bar carries Draw, and Draw is the only way a tile leaves the wall, so it going off the
    // bottom is not a cosmetic problem - it is a table nobody can play. It used to: the page was
    // `min-height: 100dvh` and anything that grew above the bar pushed it past the fold, which on
    // a phone is a scroll nobody knows to make. The page is exactly one screen now, and the parts
    // that can grow scroll inside themselves.
    await expect(page.getByTestId('turn-bar')).toBeInViewport();
    await expect(page.getByTestId('draw')).toBeInViewport();
    await noPageScroll(page);

    // Opening all three opponents' hands is the biggest thing that can happen above the bar: it is
    // forty-eight tiles, and on a phone they wrap into rows that used to come out of the bottom.
    const eyes = page.locator('.opponent .eye');
    for (let i = 0; i < (await eyes.count()); i++) await eyes.nth(i).click();

    await expect(page.locator('.opponent .hidden-hand mj-tile').first()).toBeVisible();
    await expect(page.getByTestId('draw')).toBeInViewport();
    await page.screenshot({ path: shot('05-table-revealed') });
    await noSidewaysScroll(page);
    await noPageScroll(page);

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

/**
 * The table does not scroll vertically either. Everywhere else on the site a long page is fine;
 * here it is how the action bar goes missing, because the bar is the last row of the page and a
 * page taller than the screen puts its last row below the fold.
 */
async function noPageScroll(page: import('@playwright/test').Page): Promise<void> {
  const overflow = await page.evaluate(() => {
    const doc = document.documentElement;
    return { scrollHeight: doc.scrollHeight, clientHeight: doc.clientHeight };
  });

  expect(overflow.scrollHeight).toBeLessThanOrEqual(overflow.clientHeight + 1);
}

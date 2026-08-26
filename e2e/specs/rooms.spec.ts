import { expect, test } from '@playwright/test';
import { PASSWORD, closeAll, createRoom, joinRoom } from './helpers';

test.describe('creating a table and sitting down', () => {
  test('the host creates a table and gets an invite link', async ({ browser }) => {
    const { host, code } = await createRoom(browser, { roomName: 'Lola Tables' });

    await expect(host.page.getByTestId('lobby-code')).toHaveText(code);
    await expect(host.page.getByRole('heading', { name: 'Lola Tables' })).toBeVisible();

    // The link must point at the address this browser actually reached the app on, otherwise it
    // sends the other players somewhere they cannot get to.
    const invite = await host.page.getByTestId('invite-url').textContent();
    expect(invite).toContain(`/join/${code}`);
    expect(invite).toContain(new URL(host.page.url()).host);

    await expect(host.page.getByTestId('invite-qr')).toBeVisible();
    await expect(host.page.getByTestId('seat-0-name')).toHaveText('Mynard');

    await host.context.close();
  });

  test('a second player joins through the invite link', async ({ browser }) => {
    const { host, code } = await createRoom(browser);
    const guest = await joinRoom(browser, code, 'Tito Ben');

    await expect(guest.page.getByTestId('seat-1-name')).toHaveText('Tito Ben');

    // The host's lobby polls, so the new player shows up without a reload.
    await expect(host.page.getByTestId('seat-1-name')).toHaveText('Tito Ben', { timeout: 10_000 });

    await closeAll([host, guest]);
  });

  test('a player joins from the start page by typing the code', async ({ browser }) => {
    const { host, code } = await createRoom(browser);

    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto('/');
    await page.getByTestId('mode-join').click();

    // Typed the way a code gets read out across the room: lower case, with a space in it.
    const spoken = `${code.slice(0, 3)} ${code.slice(3)}`.toLowerCase();
    await page.getByTestId('join-code-input').fill(spoken);
    await page.getByTestId('join-name').fill('Ate Rose');
    await page.getByTestId('join-password').fill(PASSWORD);
    await page.getByTestId('join-submit').click();

    await expect(page).toHaveURL(new RegExp(`/room/${code}$`));
    await expect(page.getByTestId('seat-1-name')).toHaveText('Ate Rose');

    await context.close();
    await host.context.close();
  });

  test('a made-up code typed on the start page is refused', async ({ browser }) => {
    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto('/');
    await page.getByTestId('mode-join').click();
    await page.getByTestId('join-code-input').fill('ZZZZZZ');
    await page.getByTestId('join-name').fill('Nobody');
    await page.getByTestId('join-password').fill(PASSWORD);
    await page.getByTestId('join-submit').click();

    await expect(page.getByTestId('join-error')).toContainText('ZZZZZZ');
    await expect(page).toHaveURL(/\/$/);

    await context.close();
  });

  test('the wrong password is refused', async ({ browser }) => {
    const { host, code } = await createRoom(browser);

    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto(`/join/${code}`);
    await page.getByTestId('join-name').fill('Impostor');
    await page.getByTestId('join-password').fill('not-the-password');
    await page.getByTestId('join-submit').click();

    await expect(page.getByTestId('join-error')).toContainText('password');
    await expect(page).toHaveURL(new RegExp(`/join/${code}$`));

    // And the seat stays free.
    await expect(host.page.getByTestId('seat-1-name')).toHaveText('waiting...');

    await context.close();
    await host.context.close();
  });

  test('an unknown room code is refused', async ({ browser }) => {
    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto('/join/ZZZZZZ');
    await expect(page.getByTestId('join-lookup-error')).toContainText('No table');

    await context.close();
  });

  test('four players take four different seats', async ({ browser }) => {
    const { host, code } = await createRoom(browser);

    const others = await Promise.all([
      joinRoom(browser, code, 'Tito Ben'),
      joinRoom(browser, code, 'Ate Rose'),
      joinRoom(browser, code, 'Kuya Jun'),
    ]);

    const everyone = [host, ...others];
    const expected = ['Mynard', 'Tito Ben', 'Ate Rose', 'Kuya Jun'];

    // The three guests join at the same instant and race for seats, so which name lands on which
    // seat is not fixed - and should not be. What has to hold is that all four are seated, each
    // exactly once, with nobody sharing a seat and no seat left empty.
    for (const player of everyone) {
      await expect(player.page.getByTestId('seat-list').locator('li')).toHaveCount(4);

      const seated = async () =>
        (await player.page.getByTestId('seat-list').locator('.name').allTextContents())
          .map((n) => n.trim())
          .sort();

      await expect.poll(seated, { timeout: 10_000 }).toEqual([...expected].sort());
    }

    // The host is whoever made the table, whatever seat the others ended up in.
    await expect(host.page.getByTestId('seat-0-name')).toHaveText('Mynard');

    // A fifth player has nowhere to sit.
    const context = await browser.newContext();
    const page = await context.newPage();
    await page.goto(`/join/${code}`);
    await page.getByTestId('join-name').fill('Late');
    await page.getByTestId('join-password').fill(PASSWORD);
    await page.getByTestId('join-submit').click();
    await expect(page.getByTestId('join-error')).toContainText('seats are taken');
    await context.close();

    await closeAll(everyone);
  });

  test('only the host is offered the deal button', async ({ browser }) => {
    const { host, code } = await createRoom(browser);
    const guest = await joinRoom(browser, code, 'Tito Ben');

    await expect(host.page.getByTestId('start-hand')).toBeVisible();
    await expect(guest.page.getByTestId('start-hand')).toHaveCount(0);
    await expect(guest.page.getByTestId('waiting-for-host')).toBeVisible();

    await closeAll([host, guest]);
  });

  test('reloading the lobby keeps the same seat', async ({ browser }) => {
    const { host, code } = await createRoom(browser);
    const guest = await joinRoom(browser, code, 'Tito Ben');

    await guest.page.reload();

    // Straight back into the lobby, not bounced to the join form.
    await expect(guest.page).toHaveURL(new RegExp(`/room/${code}$`));
    await expect(guest.page.getByTestId('seat-1-name')).toHaveText('Tito Ben');

    await closeAll([host, guest]);
  });
});

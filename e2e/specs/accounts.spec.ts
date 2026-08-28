import { expect, test } from '@playwright/test';
import { PASSWORD } from './helpers';

/** A password for an account, which the server holds to a longer minimum than a table password. */
const ACCOUNT_PASSWORD = 'mahjong-account-1';

/**
 * A name nobody has registered yet.
 *
 * The database is not reset between runs, and a username is first come, first served - so a fixed
 * name would pass once and then be taken forever by the run that passed. Every run claims its own.
 */
function freshUsername(): string {
  return `mj${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
}

test.describe('accounts', () => {
  test('registering claims a username and opens an empty profile', async ({ browser }) => {
    const username = freshUsername();
    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto('/register');
    await page.getByTestId('account-username').fill(username);
    await page.getByTestId('account-password').fill(ACCOUNT_PASSWORD);
    await page.getByTestId('account-submit').click();

    await expect(page).toHaveURL(/\/me$/);
    await expect(page.getByTestId('profile-username')).toHaveText(username);

    // Nothing has been played yet, so the profile says so rather than showing an empty list.
    await expect(page.getByTestId('profile-empty')).toBeVisible();

    // Signed in, the start page offers the way back to the profile instead of the way to register.
    await page.goto('/');
    await expect(page.getByTestId('account-profile')).toBeVisible();

    await context.close();
  });

  test('a username is first come, first served, whatever case it is typed in', async ({ browser }) => {
    const username = freshUsername();

    const first = await browser.newContext();
    const firstPage = await first.newPage();

    await firstPage.goto('/register');
    await firstPage.getByTestId('account-username').fill(username);
    await firstPage.getByTestId('account-password').fill(ACCOUNT_PASSWORD);
    await firstPage.getByTestId('account-submit').click();
    await expect(firstPage).toHaveURL(/\/me$/);

    // Somebody else, in their own browser, going for the same name shouted back at them.
    const second = await browser.newContext();
    const secondPage = await second.newPage();

    await secondPage.goto('/register');
    await secondPage.getByTestId('account-username').fill(username.toUpperCase());
    await secondPage.getByTestId('account-password').fill('another-password');
    await secondPage.getByTestId('account-submit').click();

    await expect(secondPage.getByTestId('account-error')).toBeVisible();
    await expect(secondPage).toHaveURL(/\/register$/);

    await first.close();
    await second.close();
  });

  test('signing out and back in again returns to the same profile', async ({ browser }) => {
    const username = freshUsername();
    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto('/register');
    await page.getByTestId('account-username').fill(username);
    await page.getByTestId('account-password').fill(ACCOUNT_PASSWORD);
    await page.getByTestId('account-submit').click();
    await expect(page).toHaveURL(/\/me$/);

    await page.getByTestId('profile-signout').click();
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByTestId('account-register')).toBeVisible();

    // The profile is not readable by a browser that is no longer signed in.
    await page.goto('/me');
    await expect(page).toHaveURL(/\/sign-in$/);

    await page.getByTestId('account-username').fill(username);
    await page.getByTestId('account-password').fill(ACCOUNT_PASSWORD);
    await page.getByTestId('account-submit').click();

    await expect(page).toHaveURL(/\/me$/);
    await expect(page.getByTestId('profile-username')).toHaveText(username);

    await context.close();
  });

  test('a wrong password does not sign anybody in', async ({ browser }) => {
    const username = freshUsername();
    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto('/register');
    await page.getByTestId('account-username').fill(username);
    await page.getByTestId('account-password').fill(ACCOUNT_PASSWORD);
    await page.getByTestId('account-submit').click();
    await expect(page).toHaveURL(/\/me$/);

    await page.getByTestId('profile-signout').click();
    await expect(page).toHaveURL(/\/$/);

    await page.goto('/sign-in');
    await page.getByTestId('account-username').fill(username);
    await page.getByTestId('account-password').fill('not-the-password');
    await page.getByTestId('account-submit').click();

    await expect(page.getByTestId('account-error')).toBeVisible();
    await expect(page).toHaveURL(/\/sign-in$/);

    await context.close();
  });

  test('a table made while signed in is played under the account name', async ({ browser }) => {
    const username = freshUsername();
    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto('/register');
    await page.getByTestId('account-username').fill(username);
    await page.getByTestId('account-password').fill(ACCOUNT_PASSWORD);
    await page.getByTestId('account-submit').click();
    await expect(page).toHaveURL(/\/me$/);

    // The account name is offered as the name to play under, so the create form needs nothing but
    // a table name and a password.
    await page.goto('/');
    await expect(page.getByTestId('display-name')).toHaveValue(username);

    await page.getByTestId('room-name').fill('Signed-in table');
    await page.getByTestId('room-password').fill(PASSWORD);
    await page.getByTestId('create-submit').click();

    await expect(page).toHaveURL(/\/room\/[A-Z0-9]{6}$/);
    await expect(page.getByTestId('seat-0-name')).toHaveText(username);

    await context.close();
  });
});

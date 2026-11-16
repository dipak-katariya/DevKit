// Shared mode: has the browser drop the settings an earlier DevKit kept sealed in its cookies. They are never moved to
// the server, since a cookie cannot show that DevKit made it (EarlierSettingsCopies.cs).

const VAULT_ADDRESS = '/devkit-vault';

export async function clearBrowser() {
    var response = await fetch(VAULT_ADDRESS, { method: 'DELETE', credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok) {
        throw new Error('The settings kept in this browser could not be cleared (HTTP ' + response.status + ').');
    }
}

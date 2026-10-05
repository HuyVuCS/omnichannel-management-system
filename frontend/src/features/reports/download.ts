/**
 * Follows a download link the way a click on it would: through an anchor that exists for the click only. The storage
 * serves an export as an attachment (`Content-Disposition`), so the browser saves the file and the page stays where it
 * is; no window is opened. `noreferrer`: the storage does not learn which page the link came from. The link is a
 * credential for a few minutes: it goes to the browser and nowhere else - not into the address, the store, a message
 * or the console.
 */
export function followDownloadLink(url: string): void {
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.rel = 'noreferrer';
  anchor.style.display = 'none';
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
}

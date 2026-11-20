// The list of connections on the start page. On a wide screen the side panel has it, and the page beside
// the panel shows quick connect only; with the panel put away the page is the list again. The scripts that
// are about that list say so with this, before they open the page: the panel starts put away, as for
// somebody who closed it (a script that opens it itself is not overruled).
module.exports = target => target.addInitScript(() => {
  try {
    if (window.localStorage.getItem("matgate.serverManager.open") === null) {
      window.localStorage.setItem("matgate.serverManager.open", "false");
    }
  }
  catch { /* no storage: then the panel is there */ }
});

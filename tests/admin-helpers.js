// What several scripts do in the administration since 0.46.0, where servers and folders are one tree
// (Administration -> Connections): find a folder in it, and dissolve folders a test made.

// Is this folder (its path, "Lab/Linux") in the administration's tree?
const folderInTree = (page, path) => page.evaluate(async p => {
  const text = await (await fetch("/admin?tab=connections", { credentials: "same-origin" })).text();
  return !!new DOMParser().parseFromString(text, "text/html").querySelector("[data-admtree-folder='" + p.replace(/'/g, "\\'") + "']");
}, path);

// The folders of the tree, by path.
const foldersInTree = page => page.evaluate(async () => {
  const text = await (await fetch("/admin?tab=connections", { credentials: "same-origin" })).text();
  return [...new DOMParser().parseFromString(text, "text/html").querySelectorAll("[data-admtree-folder]")].map(group => group.dataset.admtreeFolder).filter(Boolean);
});

// Dissolves folders by their path, as the folder's "Dissolve" in the tree does. Their connections stay.
const dissolveFolders = (page, paths) => page.evaluate(async list => {
  const text = await (await fetch("/", { credentials: "same-origin" })).text();
  const token = JSON.parse(text.match(/const csrfToken = ("[^"]*")/)[1]);
  for (const name of list) {
    const body = new FormData();
    body.append("_csrf", token);
    body.append("name", name);
    await fetch("/admin/folders/delete", { method: "POST", body, credentials: "same-origin", redirect: "manual" });
  }
}, paths);

module.exports = { folderInTree, foldersInTree, dissolveFolders };

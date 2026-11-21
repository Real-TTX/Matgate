// Chooses the kind of a connection the way a person does: open the field, pick the entry in the list.
// `scope` is a selector for what holds the field (the form, the quick-connect dialog); `value` is what the
// option carries ("Ssh" in the form, "ssh" in quick connect).
module.exports = async function chooseKind(page, scope, value) {
  await page.click(scope + " [data-kind-button]");
  await page.click(".kind-menu .toolbar-menu-item[data-value='" + value + "']");
};

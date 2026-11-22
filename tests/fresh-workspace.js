// Every page of this browser starts without tabs: since 0.38.0 a page brings back the tabs another page of
// the same browser left behind - a test that opens one connection per page wants that connection alone. Only
// a page of its own starts empty; reloading it keeps its tabs.
module.exports = target => target.addInitScript(() => {
  try {
    if (window.top === window && !sessionStorage.getItem("matgate.test.fresh")) {
      sessionStorage.setItem("matgate.test.fresh", "1");
      localStorage.removeItem("matgate.workspace.tabs.v2");
    }
  }
  catch { /* a frame without storage */ }
});

# CodeMirror for the database browser

`../codemirror.js` is CodeMirror 6 (MIT, see `../LICENSE`) as one file. It holds only what `entry.js` takes,
and it puts that on `window.MatgateCodeMirror`. The versions it was built from are in `versions.json`.

To build it again:

```bash
npm init -y
npm install codemirror@6 @codemirror/lang-sql@6 @codemirror/autocomplete@6 @codemirror/language@6 \
  @codemirror/state@6 @codemirror/view@6 @codemirror/commands@6 @codemirror/search@6 @lezer/highlight esbuild
npx esbuild entry.js --bundle --minify --format=iife --legal-comments=none --outfile=../codemirror.js
```

Do this in a folder of its own: `node_modules` does not belong in the repository.

// What the database browser of Matgate uses of CodeMirror 6, as one file (window.MatgateCodeMirror).
import { EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter, drawSelection, placeholder } from "@codemirror/view";
import { EditorState, Compartment, Prec } from "@codemirror/state";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { searchKeymap, highlightSelectionMatches } from "@codemirror/search";
import { autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap } from "@codemirror/autocomplete";
import { syntaxHighlighting, HighlightStyle, bracketMatching, indentOnInput } from "@codemirror/language";
import { tags } from "@lezer/highlight";
import { sql, PostgreSQL, MySQL, MSSQL, MariaSQL, StandardSQL } from "@codemirror/lang-sql";
window.MatgateCodeMirror = {
  EditorView, EditorState, Compartment, Prec, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter, drawSelection, placeholder,
  defaultKeymap, history, historyKeymap, indentWithTab, searchKeymap, highlightSelectionMatches,
  autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap,
  syntaxHighlighting, HighlightStyle, bracketMatching, indentOnInput, tags,
  sql, dialects: { postgres: PostgreSQL, mysql: MySQL, mariadb: MariaSQL, mssql: MSSQL, standard: StandardSQL },
};

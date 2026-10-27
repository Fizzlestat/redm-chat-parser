namespace RedMChatParser;

internal static class ChatPageScript
{
    public const string HasChatRoot = """(() => document.querySelector(".chat__messages, .chat-messages") ? "1" : "")()""";

    public const string ExtractLines = """
        (() => {
          const timePattern = /\b\d{1,2}:\d{2}:\d{2}\b/;
          const state = globalThis.__redmChatRead || (globalThis.__redmChatRead = {
            dirty: true,
            seen: false,
            root: null,
            obs: null,
            cache: new WeakMap(),
            changed: new WeakSet(),
            memo: new Map()
          });

          const root = document.querySelector(".chat__messages, .chat-messages");
          if (!root) {
            if (state.obs) state.obs.disconnect();
            state.obs = null;
            state.root = null;
            state.seen = false;
            state.dirty = true;
            return "[]";
          }

          const legacy = root.classList.contains("chat__messages");
          const rowOf = (node) => {
            let el = node;
            if (!el || el === root) return null;
            if (el.nodeType === 3) el = el.parentElement;
            if (!el || el.nodeType !== 1 || el === root) return null;
            const row = legacy
              ? (el.tagName === "LI" && el.parentElement === root ? el : el.closest("li"))
              : (el.classList.contains("msg") ? el : el.closest(".msg"));
            if (!row || row === root || !root.contains(row)) return null;
            if (legacy && row.parentElement !== root) return null;
            return row;
          };

          const note = (node) => {
            const row = rowOf(node);
            if (row) state.changed.add(row);
          };

          if (state.root !== root) {
            if (state.obs) state.obs.disconnect();
            state.root = root;
            state.dirty = true;
            state.seen = false;
            state.cache = new WeakMap();
            state.changed = new WeakSet();
            state.obs = new MutationObserver((records) => {
              for (let i = 0; i < records.length; i++) {
                const record = records[i];
                if (record.target === root && record.type === "attributes") continue;
                state.dirty = true;
                if (record.type === "childList") {
                  note(record.target);
                  for (let n = 0; n < record.addedNodes.length; n++) note(record.addedNodes[n]);
                  continue;
                }

                note(record.target);
              }
            });
            state.obs.observe(root, {
              subtree: true,
              childList: true,
              characterData: true,
              attributes: true
            });
          }

          if (state.seen && !state.dirty) return "[]";

          const changed = state.changed;
          state.changed = new WeakSet();
          state.dirty = false;

          const rgbOf = (value) => {
            const match = String(value || "").match(/rgba?\(\s*(\d+)[,\s]+(\d+)[,\s]+(\d+)(?:[,\s/]+([0-9.]+))?/i);
            if (!match) return null;
            if (match[4] !== undefined && Number(match[4]) === 0) return null;
            return [Number(match[1]), Number(match[2]), Number(match[3])];
          };

          const codeFor = (rgb) => "^#" + rgb.map((part) => part.toString(16).padStart(2, "0")).join("").toUpperCase();
          const collapse = (value) => String(value || "").replace(/\s+/g, " ").trim();

          const withCodes = (row) => {
            let text = "";
            let last = "";
            const emit = (key) => {
              if (!key || key === last) return;
              text += codeFor(key.split(",").map(Number));
              last = key;
            };
            const visit = (node, parentKey) => {
              if (node.nodeType === 3) {
                const value = node.textContent || "";
                if (!value) return;
                emit(parentKey);
                text += value;
                return;
              }

              if (node.nodeType !== 1) return;
              const style = getComputedStyle(node);
              if (style.display === "none" || style.visibility === "hidden") return;

              const rgb = rgbOf(style.color);
              const key = rgb ? rgb.join(",") : parentKey;
              for (let child = node.firstChild; child; child = child.nextSibling) visit(child, key);
            };

            visit(row, "");
            return collapse(text);
          };

          const findTime = (row, text) => {
            if (/^\[\d{1,2}:\d{2}:\d{2}\]\s+/.test(text)) return "";

            const nodes = [row];
            const nested = row.getElementsByTagName("*");
            for (let i = 0; i < nested.length; i++) nodes.push(nested[i]);

            for (let i = 0; i < nodes.length; i++) {
              const node = nodes[i];
              const attributes = node.attributes;
              if (attributes) {
                for (let a = 0; a < attributes.length; a++) {
                  const match = String(attributes[a].value).match(timePattern);
                  if (match) return match[0];
                }
              }

              const before = String(getComputedStyle(node, "::before").content || "");
              const match = before.match(timePattern);
              if (match) return match[0];
            }

            return "";
          };

          const extract = (row) => {
            const text = collapse(row.innerText);
            if (!text) return null;

            const timestamp = findTime(row, text);
            const prefix = timestamp ? "[" + timestamp + "] " : "";
            const colored = withCodes(row);
            return {
              plain: prefix + text,
              colored: prefix + (colored || text)
            };
          };

          const remember = (html, line) => {
            if (!line) return;
            if (state.memo.size > 4000) state.memo.clear();
            state.memo.set(html, line);
          };

          const rows = [];
          if (legacy) {
            for (let child = root.firstElementChild; child; child = child.nextElementSibling) {
              if (child.tagName === "LI") rows.push(child);
            }
          } else {
            const msgs = root.querySelectorAll(".msg");
            for (let i = 0; i < msgs.length; i++) rows.push(msgs[i]);
          }

          const lines = [];
          for (let r = 0; r < rows.length; r++) {
            const child = rows[r];

            if (state.cache.get(child) !== undefined && !changed.has(child)) {
              const cached = state.cache.get(child);
              if (cached) lines.push(cached);
              continue;
            }

            const html = child.innerHTML;
            const remembered = state.memo.get(html);
            if (remembered !== undefined) {
              state.cache.set(child, remembered);
              if (remembered) lines.push(remembered);
              continue;
            }

            const line = extract(child);
            state.cache.set(child, line);
            remember(html, line);
            if (line) lines.push(line);
          }

          state.seen = true;
          return JSON.stringify(lines);
        })()
        """;
}
